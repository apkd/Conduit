using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ProjectSingleFlightTests
{
    [Test]
    public async Task ConcurrentCallersForTheSameProjectShareOneExecution()
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var executionCount = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            async (_, _) =>
            {
                Interlocked.Increment(ref executionCount);
                await release.Task;
                return 42;
            },
            CancellationToken.None,
            CancellationToken.None
        );

        var second = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            async (_, _) =>
            {
                Interlocked.Increment(ref executionCount);
                await release.Task;
                return 99;
            },
            CancellationToken.None,
            CancellationToken.None
        );

        release.SetResult();

        await Assert.That(await first).IsEqualTo(42);
        await Assert.That(await second).IsEqualTo(42);
        await Assert.That(executionCount).IsEqualTo(1);
    }

    [Test]
    public async Task CallerCancellationDoesNotCancelTheSharedExecution()
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerCts = new CancellationTokenSource();

        var cancelledCaller = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            async (_, _) =>
            {
                await release.Task;
                return 7;
            },
            CancellationToken.None,
            callerCts.Token
        );

        var waitingCaller = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            async (_, _) =>
            {
                await release.Task;
                return 11;
            },
            CancellationToken.None,
            CancellationToken.None
        );

        callerCts.Cancel();
        try
        {
            await cancelledCaller;
            throw new InvalidOperationException("The cancelled caller should not complete successfully.");
        }
        catch (OperationCanceledException) { }

        release.SetResult();

        await Assert.That(await waitingCaller).IsEqualTo(7);
    }
}
