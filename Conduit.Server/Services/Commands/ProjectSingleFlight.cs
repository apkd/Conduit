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
        if (inFlightOperations.TryGetValue(normalizedProjectPath, out var existingOperation))
            return existingOperation.Value.WaitAsync(callerCancellationToken);

        // assign the captured entry before publication so cleanup cannot remove a later attempt.
        Lazy<Task<TResult>> newOperation = null!;
        newOperation = new(ExecuteAsync, LazyThreadSafetyMode.ExecutionAndPublication);

        return inFlightOperations
            .GetOrAdd(normalizedProjectPath, newOperation)
            .Value.WaitAsync(callerCancellationToken);

        async Task<TResult> ExecuteAsync()
        {
            try
            {
                return await operationFactory(normalizedProjectPath, operationCancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // evict before publishing completion, including synchronous factory failures.
                inFlightOperations.TryRemove(
                    new KeyValuePair<string, Lazy<Task<TResult>>>(normalizedProjectPath, newOperation)
                );
            }
        }
    }
}
