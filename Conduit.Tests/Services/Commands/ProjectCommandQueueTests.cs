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
}
