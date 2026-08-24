using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    public Task<ToolExecutionResult> RunTestsEditModeAsync(string projectPath, string? testFilter, bool @async, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsEditMode,
                TestFilter = testFilter,
                Async = @async ? true : null,
            },
            ct
        );

    public Task<ToolExecutionResult> RunTestsPlayModeAsync(string projectPath, string? testFilter, bool @async, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsPlayMode,
                TestFilter = testFilter,
                Async = @async ? true : null,
            },
            ct
        );

    public Task<ToolExecutionResult> RunTestsPlayerAsync(string projectPath, string? testFilter, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsPlayer,
                TestFilter = testFilter,
            },
            ct
        );
}
