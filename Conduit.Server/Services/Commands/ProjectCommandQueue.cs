using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

sealed class ProjectCommandQueue
{
    const string shutdownDiagnostic = "The MCP server is shutting down.";

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

        Completion = ProcessAsync();
    }

    // shutdown settles requests without waiting for uncooperative executor work to finish.
    internal Task Completion { get; }

    public async Task<ToolExecutionResult> EnqueueAsync(QueuedProjectCommand command, CancellationToken ct)
    {
        command.Session.IncrementQueuedCount();
#if DEBUG
        LogQueueDepthIfNeeded(Interlocked.Increment(ref queuedDepth), command.Session.ProjectPath);
#endif

        if (ct.IsCancellationRequested || shutdownToken.IsCancellationRequested || !channel.Writer.TryWrite(command))
        {
            DecrementQueuedCount(command);
            var result = ToolExecutionResult.Cancelled(
                ct.IsCancellationRequested
                    ? "The request was cancelled before it entered the Unity queue."
                    : shutdownDiagnostic
            );
            command.TrySetResult(result);
            return result;
        }

        if (!ct.CanBeCanceled)
            return await command.Completion.Task;

        try
        {
            return await command.Completion.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var commandKind = BridgeCommandKinds.Parse(command.Command.CommandType);
            if (BridgeCommandKinds.SupportsCancellation(commandKind))
            {
                var diagnostic = commandKind == BridgeCommandKind.Record
                    ? "The request was cancelled. Any active recording continues in the background."
                    : "The request was cancelled. Unity is stopping the test run.";
                logger.ZLogWarning(
                    $"Queued cancellable Unity command '{command.Command.CommandType}' for project {command.Session.ProjectPath} was cancelled by the caller."
                );
                return ToolExecutionResult.Cancelled(diagnostic);
            }

            logger.ZLogWarning(
                $"Queued Unity command '{command.Command.CommandType}' for project {command.Session.ProjectPath} was cancelled by the caller while Unity work kept running."
            );

            return ToolExecutionResult.Cancelled("The request was cancelled while Unity work kept running.");
        }
    }

    async Task ProcessAsync()
    {
        try
        {
            await foreach (var command in channel.Reader.ReadAllAsync(shutdownToken))
            {
                DecrementQueuedCount(command);

                if (shutdownToken.IsCancellationRequested)
                {
                    command.TrySetResult(ToolExecutionResult.Cancelled(shutdownDiagnostic));
                    break;
                }

                if (command.RequestCancellation.IsCancellationRequested)
                {
                    command.TrySetResult(ToolExecutionResult.Cancelled("The request was cancelled before Unity work started."));
                    continue;
                }

                try
                {
                    // only shutdown may release the queue before the executor finishes.
                    var result = await ExecuteAsync(command).WaitAsync(shutdownToken);
                    command.TrySetResult(result);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    command.TrySetResult(ToolExecutionResult.Cancelled(shutdownDiagnostic));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested) { }
        finally
        {
            // close admission before draining so a racing producer cannot strand a command.
            channel.Writer.TryComplete();
            while (channel.Reader.TryRead(out var command))
            {
                DecrementQueuedCount(command);
                command.TrySetResult(ToolExecutionResult.Cancelled(shutdownDiagnostic));
            }
        }

        async Task<ToolExecutionResult> ExecuteAsync(QueuedProjectCommand command)
        {
            // observe failures even if shutdown has already ended the queue's wait.
            try
            {
                return await executor(command, shutdownToken);
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                return ToolExecutionResult.Cancelled(shutdownDiagnostic);
            }
            catch (Exception exception)
            {
                logger.ZLogError(exception, $"Queued Unity command failed for project {command.Session.ProjectPath}.");
                return ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    "The MCP server failed while processing the queued Unity command."
                );
            }
        }
    }

    void DecrementQueuedCount(QueuedProjectCommand command)
    {
        command.Session.DecrementQueuedCount();
#if DEBUG
        Interlocked.Decrement(ref queuedDepth);
#endif
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
