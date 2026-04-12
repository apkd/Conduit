using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

sealed class ProjectCommandQueue
{
    readonly Channel<QueuedProjectCommand> channel;
    readonly Func<QueuedProjectCommand, CancellationToken, Task<ToolExecutionResult>> executor;
    readonly CancellationToken shutdownToken;
    readonly ILogger<ProjectCommandQueue> logger;
#if DEBUG
    int queuedDepth;
    int lastLoggedQueueDepth;
#endif

    public ProjectCommandQueue(
        ILogger<ProjectCommandQueue> logger,
        Func<QueuedProjectCommand, CancellationToken, Task<ToolExecutionResult>> executor,
        CancellationToken shutdownToken
    )
    {
        this.logger = logger;
        this.executor = executor;
        this.shutdownToken = shutdownToken;
        channel = Channel.CreateUnbounded<QueuedProjectCommand>(
            new()
            {
                SingleReader = true,
                SingleWriter = false,
            }
        );

        _ = Task.Run(ProcessAsync, shutdownToken);
    }

    public async Task<ToolExecutionResult> EnqueueAsync(QueuedProjectCommand command, CancellationToken ct)
    {
        command.Session.IncrementQueuedCount();
#if DEBUG
        LogQueueDepthIfNeeded(Interlocked.Increment(ref queuedDepth), command.Session.ProjectPath);
#endif

        try
        {
            await channel.Writer.WriteAsync(command, ct);
        }
        catch (OperationCanceledException)
        {
            command.Session.DecrementQueuedCount();
#if DEBUG
            Interlocked.Decrement(ref queuedDepth);
#endif
            return ToolExecutionResult.Cancelled("The request was cancelled before it entered the Unity queue.");
        }

        if (!ct.CanBeCanceled)
            return await command.Completion.Task;

        var callerCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = ct.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            callerCancellation
        );

        var completedTask = await Task.WhenAny(command.Completion.Task, callerCancellation.Task);
        if (completedTask != command.Completion.Task)
        {
            logger.ZLogWarning(
                $"Queued Unity command '{command.Command.CommandType}' for project {command.Session.ProjectPath} was cancelled by the caller while Unity work kept running."
            );

            return ToolExecutionResult.Cancelled("The request was cancelled while Unity work kept running.");
        }

        return await command.Completion.Task;
    }

    async Task ProcessAsync()
    {
        try
        {
            await foreach (var command in channel.Reader.ReadAllAsync(shutdownToken))
            {
                command.Session.DecrementQueuedCount();
#if DEBUG
                Interlocked.Decrement(ref queuedDepth);
#endif

                if (command.RequestCancellation.IsCancellationRequested)
                {
                    command.TrySetResult(ToolExecutionResult.Cancelled("The request was cancelled before Unity work started."));
                    continue;
                }

                try
                {
                    using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                    var result = await executor(command, executionCts.Token);
                    command.TrySetResult(result);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    command.TrySetResult(ToolExecutionResult.Cancelled("The MCP server is shutting down."));
                    break;
                }
                catch (Exception exception)
                {
                    logger.ZLogError($"Queued Unity command failed for project {command.Session.ProjectPath}.", exception);
                    command.TrySetResult(
                        ToolExecutionResult.FromException(
                            exception,
                            string.Empty,
                            "The MCP server failed while processing the queued Unity command."
                        )
                    );
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
    }

#if DEBUG
    void LogQueueDepthIfNeeded(int depth, string projectPath)
    {
        const int warningThreshold = 8;
        if (depth < warningThreshold)
            return;

        if (depth <= Volatile.Read(ref lastLoggedQueueDepth))
            return;

        Volatile.Write(ref lastLoggedQueueDepth, depth);
        logger.ZLogDebug($"Unity command queue depth reached {depth} for project {projectPath}.");
    }
#endif
}

sealed class QueuedProjectCommand(
    ProjectSession session,
    BridgeCommand command,
    CancellationToken requestCancellation)
{
    public ProjectSession Session { get; } = session;

    public BridgeCommand Command { get; } = command;

    public CancellationToken RequestCancellation { get; } = requestCancellation;

    public TaskCompletionSource<ToolExecutionResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TrySetResult(ToolExecutionResult result) => Completion.TrySetResult(result);
}
