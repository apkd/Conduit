using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    public Task<ToolExecutionResult> ProjectSettingsAsync(
        string projectPath,
        string key,
        string operation,
        string? value,
        CT ct
    )
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.ProjectSettings,
                Target = key,
                Snippet = value ?? "null",
                Args = [operation],
            },
            ct: ct
        );
}
