using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit;

[Timeout(60_000)]
public sealed class ProjectCommandQueueTests
{
    [Test]
    public async Task RecordCallerCancellationReportsThatBackgroundCaptureContinues(CancellationToken ct)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ProjectSession(@"B:\Projects\Sample");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = new ProjectCommandQueue(
            NullLogger<ProjectCommandQueue>.Instance,
            async (_, ct) =>
            {
                started.TrySetResult(true);
                await release.Task.WaitAsync(ct);
                finished.TrySetResult(true);
                return ToolExecutionResult.Success("finished");
            },
            shutdown.Token
        );

        using var callerCancellation = new CancellationTokenSource();
        var resultTask = queue.EnqueueAsync(
            new(session, new() { CommandType = BridgeCommandTypes.Record }, callerCancellation.Token),
            callerCancellation.Token
        );

        try
        {
            await started.Task.WaitAsync(ct);
            callerCancellation.Cancel();
            var result = await resultTask.WaitAsync(ct);

            await Assert.That(result.Outcome).IsEqualTo(ToolOutcome.Cancelled);
            await Assert.That(result.Diagnostic).Contains("Any active recording continues");

            release.TrySetResult(true);
            await finished.Task.WaitAsync(ct);
        }
        finally
        {
            shutdown.Cancel();
            await queue.Completion;
        }
    }

    [Test]
    public async Task CallerCancellationDoesNotAbortRunningUnityWorkOrReleaseQueueEarly(CancellationToken ct)
    {
        var firstCommandStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstCommandToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCommandStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCommandFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ProjectSession(@"B:\Projects\Sample");
        var invocationCount = 0;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = new ProjectCommandQueue(
            NullLogger<ProjectCommandQueue>.Instance,
            async (_, ct) =>
            {
                var invocationIndex = Interlocked.Increment(ref invocationCount);
                if (invocationIndex == 1)
                {
                    firstCommandStarted.TrySetResult(true);
                    await allowFirstCommandToFinish.Task.WaitAsync(ct);
                    firstCommandFinished.SetResult(true);
                    return ToolExecutionResult.Success("first");
                }

                if (!firstCommandFinished.Task.IsCompleted)
                    throw new InvalidOperationException("The second command started before the first command finished.");
                secondCommandStarted.TrySetResult(true);
                return ToolExecutionResult.Success("second");
            },
            shutdown.Token
        );

        using var callerCancellation = new CancellationTokenSource();
        var firstTask = queue.EnqueueAsync(
            new(session, new() { CommandType = BridgeCommandTypes.RefreshAssetDatabase }, callerCancellation.Token),
            callerCancellation.Token
        );

        try
        {
            await firstCommandStarted.Task.WaitAsync(ct);
            callerCancellation.Cancel();

            var cancelledResult = await firstTask.WaitAsync(ct);
            await Assert.That(cancelledResult.Outcome).IsEqualTo(ToolOutcome.Cancelled);

            var secondTask = queue.EnqueueAsync(
                new(session, new() { CommandType = BridgeCommandTypes.RunTestsEditMode }, ct),
                ct
            );

            await Assert.That(secondCommandStarted.Task.IsCompleted).IsFalse();
            allowFirstCommandToFinish.TrySetResult(true);

            var secondResult = await secondTask.WaitAsync(ct);
            await Assert.That(secondResult.Outcome).IsEqualTo(ToolOutcome.Success);
            await Assert.That(firstCommandFinished.Task.IsCompleted).IsTrue();
            await Assert.That(secondCommandStarted.Task.IsCompleted).IsTrue();
            await Assert.That(invocationCount).IsEqualTo(2);
        }
        finally
        {
            shutdown.Cancel();
            await queue.Completion;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownRejectsEnqueueWithoutLeavingSessionBusy(bool stopBeforeConstruction, CancellationToken ct)
    {
        var session = new ProjectSession(@"B:\Projects\Sample");
        session.MarkReachable(true);
        var invocationCount = 0;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (stopBeforeConstruction)
            shutdown.Cancel();
        var queue = new ProjectCommandQueue(
            NullLogger<ProjectCommandQueue>.Instance,
            (_, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                return Task.FromResult(ToolExecutionResult.Success("unexpected"));
            },
            shutdown.Token
        );

        try
        {
            shutdown.Cancel();
            await queue.Completion.WaitAsync(ct);
            var command = new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, default);

            await AssertShutdownAsync(queue.EnqueueAsync(command, CancellationToken.None), ct);
            await AssertShutdownAsync(command.Completion.Task, ct);
            await Assert.That(invocationCount).IsEqualTo(0);
            await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedIdle);
        }
        finally
        {
            shutdown.Cancel();
            await queue.Completion;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ShutdownSettlesCommandsBeforeUncooperativeExecutorFinishes(bool failLate, CancellationToken ct)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ProjectSession(@"B:\Projects\Sample");
        session.MarkReachable(true);
        var invocationCount = 0;
        var failure = new InvalidOperationException("late executor failure");
        var logger = new ExceptionLogger();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = new ProjectCommandQueue(
            logger,
            async (command, _) =>
            {
                Interlocked.Increment(ref invocationCount);
                var context = command.Session.StartCommand(command.Command);
                try
                {
                    started.TrySetResult(true);
                    await release.Task;
                    if (failLate)
                        throw failure;
                    return ToolExecutionResult.Success("finished");
                }
                finally
                {
                    command.Session.FinishCommand(context.RequestId, reachable: true);
                    finished.TrySetResult(true);
                }
            },
            shutdown.Token
        );
        var commands = Enumerable.Range(0, 3)
            .Select(_ => new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, default))
            .ToArray();
        var firstTask = queue.EnqueueAsync(commands[0], CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(ct);
            var secondTask = queue.EnqueueAsync(commands[1], CancellationToken.None);
            var thirdTask = queue.EnqueueAsync(commands[2], CancellationToken.None);
            shutdown.Cancel();

            await AssertShutdownAsync(firstTask, ct);
            await AssertShutdownAsync(secondTask, ct);
            await AssertShutdownAsync(thirdTask, ct);
            foreach (var command in commands)
                await AssertShutdownAsync(command.Completion.Task, ct);
            await queue.Completion.WaitAsync(ct);
            await Assert.That(finished.Task.IsCompleted).IsFalse();
            await Assert.That(invocationCount).IsEqualTo(1);
            await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedBusy);

            var rejected = new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, default);
            await AssertShutdownAsync(queue.EnqueueAsync(rejected, CancellationToken.None), ct);
            await AssertShutdownAsync(rejected.Completion.Task, ct);

            release.TrySetResult(true);
            await finished.Task.WaitAsync(ct);
            if (failLate)
                await Assert.That(await logger.Failure.Task.WaitAsync(ct)).IsEqualTo(failure);
            await AssertShutdownAsync(commands[0].Completion.Task, ct);
            await Assert.That(invocationCount).IsEqualTo(1);
            await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedIdle);
        }
        finally
        {
            release.TrySetResult(true);
            shutdown.Cancel();
            await queue.Completion;
        }
    }

    [Test]
    public async Task ShutdownDoesNotExecuteBufferedCommandsWhenExecutorReturnsNormally(CancellationToken ct)
    {
        var session = new ProjectSession(@"B:\Projects\Sample");
        session.MarkReachable(true);
        var pending = new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, default);
        var invocationCount = 0;
        Task<ToolExecutionResult>? pendingTask = null;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ProjectCommandQueue? queue = null;
        queue = new(
            NullLogger<ProjectCommandQueue>.Instance,
            (_, _) =>
            {
                if (Interlocked.Increment(ref invocationCount) == 1)
                {
                    // leave a buffered command and return a completed task after shutdown.
                    pendingTask = queue!.EnqueueAsync(pending, CancellationToken.None);
                    shutdown.Cancel();
                }
                return Task.FromResult(ToolExecutionResult.Success("finished"));
            },
            shutdown.Token
        );

        try
        {
            var firstResult = await queue.EnqueueAsync(
                new(session, new() { CommandType = BridgeCommandTypes.Help }, default),
                CancellationToken.None
            ).WaitAsync(ct);

            await Assert.That(firstResult.Outcome).IsEqualTo(ToolOutcome.Success);
            await AssertShutdownAsync(pendingTask!, ct);
            await AssertShutdownAsync(pending.Completion.Task, ct);
            await queue.Completion.WaitAsync(ct);
            await Assert.That(invocationCount).IsEqualTo(1);
            await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedIdle);
        }
        finally
        {
            shutdown.Cancel();
            await queue.Completion;
        }
    }

    [Test]
    public async Task CallerCancellationBeforeAdmissionOrExecutionDoesNotRunCommands(CancellationToken ct)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ProjectSession(@"B:\Projects\Sample");
        session.MarkReachable(true);
        var invocationCount = 0;
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = new ProjectCommandQueue(
            NullLogger<ProjectCommandQueue>.Instance,
            async (_, token) =>
            {
                Interlocked.Increment(ref invocationCount);
                started.TrySetResult(true);
                await release.Task.WaitAsync(token);
                return ToolExecutionResult.Success("finished");
            },
            shutdown.Token
        );
        var firstTask = queue.EnqueueAsync(
            new(session, new() { CommandType = BridgeCommandTypes.Help }, default),
            CancellationToken.None
        );
        using var callerCancellation = new CancellationTokenSource();

        try
        {
            await started.Task.WaitAsync(ct);
            var pending = new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, callerCancellation.Token);
            var pendingTask = queue.EnqueueAsync(pending, callerCancellation.Token);
            callerCancellation.Cancel();
            var callerResult = await pendingTask.WaitAsync(ct);
            await Assert.That(callerResult.Outcome).IsEqualTo(ToolOutcome.Cancelled);
            await Assert.That(pending.Completion.Task.IsCompleted).IsFalse();

            var rejected = new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, callerCancellation.Token);
            var rejectedResult = await queue.EnqueueAsync(rejected, callerCancellation.Token).WaitAsync(ct);
            await Assert.That(rejectedResult.Outcome).IsEqualTo(ToolOutcome.Cancelled);
            await Assert.That(rejected.Completion.Task.IsCompletedSuccessfully).IsTrue();
            await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedBusy);

            release.TrySetResult(true);
            await Assert.That((await firstTask.WaitAsync(ct)).Outcome).IsEqualTo(ToolOutcome.Success);
            var pendingResult = await pending.Completion.Task.WaitAsync(ct);
            await Assert.That(pendingResult.Outcome).IsEqualTo(ToolOutcome.Cancelled);

            var nextResult = await queue.EnqueueAsync(
                new(session, new() { CommandType = BridgeCommandTypes.Help }, default),
                CancellationToken.None
            ).WaitAsync(ct);
            await Assert.That(nextResult.Outcome).IsEqualTo(ToolOutcome.Success);
            await Assert.That(invocationCount).IsEqualTo(2);
            await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedIdle);
        }
        finally
        {
            release.TrySetResult(true);
            shutdown.Cancel();
            await queue.Completion;
        }
    }

    [Test]
    public async Task EnqueueRacingShutdownSettlesEveryCommand(CancellationToken ct)
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new ProjectSession(@"B:\Projects\Sample");
            session.MarkReachable(true);
            var invocationCount = 0;
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var queue = new ProjectCommandQueue(
                NullLogger<ProjectCommandQueue>.Instance,
                async (_, token) =>
                {
                    Interlocked.Increment(ref invocationCount);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ToolExecutionResult.Success("unexpected");
                },
                shutdown.Token
            );
            var commands = Enumerable.Range(0, 32)
                .Select(_ => new QueuedProjectCommand(session, new() { CommandType = BridgeCommandTypes.Help }, default))
                .ToArray();
            var producers = commands.Select(command => Task.Run(async () =>
            {
                await start.Task;
                return await queue.EnqueueAsync(command, CancellationToken.None);
            })).ToArray();
            var stopTask = Task.Run(async () =>
            {
                await start.Task;
                shutdown.Cancel();
            });

            try
            {
                start.TrySetResult(true);
                await stopTask.WaitAsync(ct);
                await Task.WhenAll(producers).WaitAsync(ct);
                await queue.Completion.WaitAsync(ct);
                foreach (var producer in producers)
                    await AssertShutdownAsync(producer, ct);
                foreach (var command in commands)
                    await AssertShutdownAsync(command.Completion.Task, ct);
                await Assert.That(invocationCount <= 1).IsTrue();
                await Assert.That(session.ToListItem(DateTimeOffset.UtcNow).Status).IsEqualTo(ProjectStatus.ConnectedIdle);
            }
            finally
            {
                start.TrySetResult(true);
                shutdown.Cancel();
                await stopTask;
                await queue.Completion;
            }
        }
    }

    static async Task AssertShutdownAsync(Task<ToolExecutionResult> task, CancellationToken ct)
    {
        var result = await task.WaitAsync(ct);
        await Assert.That(result.Outcome).IsEqualTo(ToolOutcome.Cancelled);
    }

    sealed class ExceptionLogger : ILogger<ProjectCommandQueue>
    {
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Failure.TrySetResult(exception);
        }
    }
}
