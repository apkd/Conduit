namespace Conduit;

[Timeout(60_000)]
public sealed class ProjectSingleFlightTests
{
    [Test]
    public async Task ConcurrentCallersForTheSameProjectShareOneExecution(CancellationToken ct)
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
            "/mnt/b/projects/sample/",
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

        await Assert.That(await first.WaitAsync(ct)).IsEqualTo(42);
        await Assert.That(await second.WaitAsync(ct)).IsEqualTo(42);
        await Assert.That(executionCount).IsEqualTo(1);
    }

    [Test]
    public async Task CallerCancellationDoesNotCancelTheSharedExecution(CancellationToken ct)
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var executionCount = 0;
        var receivedToken = CancellationToken.None;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var callerCts = new CancellationTokenSource();

        var cancelledCaller = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            async (_, token) =>
            {
                Interlocked.Increment(ref executionCount);
                receivedToken = token;
                await release.Task.WaitAsync(token);
                return 7;
            },
            operationCts.Token,
            callerCts.Token
        );

        var waitingCaller = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            async (_, token) =>
            {
                Interlocked.Increment(ref executionCount);
                await release.Task.WaitAsync(token);
                return 11;
            },
            operationCts.Token,
            CancellationToken.None
        );

        callerCts.Cancel();
        try
        {
            await cancelledCaller.WaitAsync(ct);
            throw new InvalidOperationException("The cancelled caller should not complete successfully.");
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            await Assert.That(exception.CancellationToken).IsEqualTo(callerCts.Token);
        }

        var lateCaller = singleFlight.RunAsync(
            "/mnt/b/projects/sample/",
            (_, _) =>
            {
                Interlocked.Increment(ref executionCount);
                return Task.FromResult(13);
            },
            operationCts.Token,
            CancellationToken.None
        );

        await Assert.That(receivedToken).IsEqualTo(operationCts.Token);
        await Assert.That(waitingCaller.IsCompleted).IsFalse();
        await Assert.That(lateCaller.IsCompleted).IsFalse();
        release.SetResult();

        await Assert.That(await waitingCaller.WaitAsync(ct)).IsEqualTo(7);
        await Assert.That(await lateCaller.WaitAsync(ct)).IsEqualTo(7);
        await Assert.That(executionCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task FactoryFailureCanBeRetried(bool throwsSynchronously, bool cancelled, CancellationToken ct)
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var executionCount = 0;

        var first = RunAsync(new CancellationToken(canceled: true));
        await AssertCompletionAsync(first, cancelled ? TaskStatus.Canceled : TaskStatus.Faulted, ct);

        await Assert.That(await RunAsync(CancellationToken.None).WaitAsync(ct)).IsEqualTo(42);
        await Assert.That(executionCount).IsEqualTo(2);

        async Task<int> RunAsync(CancellationToken operationToken)
            => await singleFlight.RunAsync(
                @"B:\Projects\Sample",
                ExecuteAsync,
                operationToken,
                CancellationToken.None
            );

        Task<int> ExecuteAsync(string _, CancellationToken token)
        {
            if (Interlocked.Increment(ref executionCount) > 1)
                return Task.FromResult(42);

            if (cancelled)
            {
                if (throwsSynchronously)
                    token.ThrowIfCancellationRequested();
                return Task.FromCanceled<int>(token);
            }

            var exception = new InvalidOperationException("restart failed");
            if (throwsSynchronously)
                throw exception;
            return Task.FromException<int>(exception);
        }
    }

    [Test]
    [Arguments(TaskStatus.RanToCompletion)]
    [Arguments(TaskStatus.Faulted)]
    [Arguments(TaskStatus.Canceled)]
    public async Task CompletionIsEvictedBeforeCallersCanRetry(TaskStatus completionStatus, CancellationToken ct)
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var completion = new TaskCompletionSource<int>();
        Task<int> first = null!;

        // register first and run inline so a retry can precede a separate cleanup continuation.
        var retry = completion.Task.ContinueWith(
            async _ =>
            {
                try
                {
                    await first;
                }
                catch (InvalidOperationException) when (completionStatus == TaskStatus.Faulted) { }
                catch (OperationCanceledException) when (completionStatus == TaskStatus.Canceled) { }

                return await singleFlight.RunAsync(
                    @"B:\Projects\Sample",
                    (_, _) => Task.FromResult(42),
                    CancellationToken.None,
                    CancellationToken.None
                );
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        ).Unwrap();

        first = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            (_, _) => completion.Task,
            CancellationToken.None,
            CancellationToken.None
        );
        var second = singleFlight.RunAsync(
            "/mnt/b/projects/sample/",
            (_, _) => throw new InvalidOperationException("A joined caller must not start another execution."),
            CancellationToken.None,
            CancellationToken.None
        );
        await Assert.That(second.IsCompleted).IsFalse();

        switch (completionStatus)
        {
            case TaskStatus.RanToCompletion:
                completion.SetResult(7);
                break;
            case TaskStatus.Faulted:
                completion.SetException(new InvalidOperationException("restart failed"));
                break;
            case TaskStatus.Canceled:
                completion.SetCanceled();
                break;
        }

        await Assert.That(await retry.WaitAsync(ct)).IsEqualTo(42);
        await AssertCompletionAsync(first, completionStatus, ct);
        await AssertCompletionAsync(second, completionStatus, ct);

        var next = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            (_, _) => Task.FromResult(99),
            CancellationToken.None,
            CancellationToken.None
        );
        await Assert.That(await next.WaitAsync(ct)).IsEqualTo(99);
    }

    [Test]
    public async Task OperationCancellationIsSharedAndCanBeRetried(CancellationToken ct)
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var first = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            (_, token) => release.Task.WaitAsync(token),
            operationCts.Token,
            CancellationToken.None
        );
        var second = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            (_, _) => Task.FromResult(99),
            operationCts.Token,
            CancellationToken.None
        );

        operationCts.Cancel();
        await AssertCompletionAsync(first, TaskStatus.Canceled, ct);
        await AssertCompletionAsync(second, TaskStatus.Canceled, ct);

        var retry = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            (_, _) => Task.FromResult(42),
            CancellationToken.None,
            CancellationToken.None
        );
        await Assert.That(await retry.WaitAsync(ct)).IsEqualTo(42);
    }

    [Test]
    public async Task DifferentProjectsDoNotShareAnExecution(CancellationToken ct)
    {
        var singleFlight = new ProjectSingleFlight<int>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = singleFlight.RunAsync(
            @"B:\Projects\Sample",
            (_, _) => release.Task,
            CancellationToken.None,
            CancellationToken.None
        );
        var second = singleFlight.RunAsync(
            @"B:\Projects\Other",
            (_, _) => Task.FromResult(99),
            CancellationToken.None,
            CancellationToken.None
        );

        await Assert.That(await second.WaitAsync(ct)).IsEqualTo(99);
        await Assert.That(first.IsCompleted).IsFalse();
        release.SetResult(42);
        await Assert.That(await first.WaitAsync(ct)).IsEqualTo(42);
    }

    static async Task AssertCompletionAsync(Task<int> task, TaskStatus completionStatus, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(ct);
        }
        catch (InvalidOperationException) when (completionStatus == TaskStatus.Faulted) { }
        catch (OperationCanceledException) when (completionStatus == TaskStatus.Canceled && !ct.IsCancellationRequested) { }

        await Assert.That(task.Status).IsEqualTo(completionStatus);
    }
}
