using System.Collections.Concurrent;

namespace Conduit;

sealed class ProjectSingleFlight<TResult>
{
    readonly ConcurrentDictionary<string, Lazy<Task<TResult>>> inFlightOperations = new(StringComparer.OrdinalIgnoreCase);

    public Task<TResult> RunAsync(
        string projectPath,
        Func<string, CancellationToken, Task<TResult>> operationFactory,
        CancellationToken operationCancellationToken,
        CancellationToken callerCancellationToken
    )
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var newOperation = new Lazy<Task<TResult>>(
            () => operationFactory(normalizedProjectPath, operationCancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication
        );

        var activeOperation = inFlightOperations.GetOrAdd(normalizedProjectPath, newOperation);
        var operationTask = activeOperation.Value;
        if (ReferenceEquals(activeOperation, newOperation))
        {
            _ = operationTask.ContinueWith(
                _ => inFlightOperations.TryRemove(new KeyValuePair<string, Lazy<Task<TResult>>>(normalizedProjectPath, activeOperation)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

        return operationTask.WaitAsync(callerCancellationToken);
    }
}
