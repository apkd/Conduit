using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit;

public sealed class ProjectCommandQueueTests
{
    [Test]
    public async Task CallerCancellationDoesNotAbortRunningUnityWorkOrReleaseQueueEarly()
    {
        var firstCommandStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstCommandToFinish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCommandStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new ProjectSession(@"B:\Projects\Sample");
        var invocationCount = 0;
        var queue = new ProjectCommandQueue(
            NullLogger<ProjectCommandQueue>.Instance,
            async (_, ct) =>
            {
                var invocationIndex = Interlocked.Increment(ref invocationCount);
                if (invocationIndex == 1)
                {
                    firstCommandStarted.TrySetResult(true);
                    await allowFirstCommandToFinish.Task.WaitAsync(ct);
                    return ToolExecutionResult.Success("first");
                }

                secondCommandStarted.TrySetResult(true);
                return ToolExecutionResult.Success("second");
            },
            CancellationToken.None
        );

        using var callerCancellation = new CancellationTokenSource();
        var firstTask = queue.EnqueueAsync(
            new(session, new() { CommandType = BridgeCommandTypes.RefreshAssetDatabase }, callerCancellation.Token),
            callerCancellation.Token
        );

        await firstCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();

        var cancelledResult = await firstTask;
        await Assert.That(cancelledResult.Outcome).IsEqualTo(ToolOutcome.Cancelled);

        var secondTask = queue.EnqueueAsync(
            new(session, new() { CommandType = BridgeCommandTypes.RunTestsEditMode }, CancellationToken.None),
            CancellationToken.None
        );

        await Task.Delay(200);
        await Assert.That(secondCommandStarted.Task.IsCompleted).IsFalse();

        allowFirstCommandToFinish.TrySetResult(true);

        var secondResult = await secondTask;
        await Assert.That(secondResult.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(secondCommandStarted.Task.IsCompleted).IsTrue();
        await Assert.That(invocationCount).IsEqualTo(2);
    }
}
