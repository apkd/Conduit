using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    public Task<ToolExecutionResult> GetDependenciesAsync(string projectPath, string asset, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.GetDependencies,
                Target = asset,
            },
            ct: ct
        );

    public Task<ToolExecutionResult> FindReferencesToAsync(string projectPath, string asset, bool rebuildCache, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.FindReferencesTo,
                Target = asset,
                RebuildCache = rebuildCache,
            },
            ct: ct
        );

    public Task<ToolExecutionResult> FindMissingScriptsAsync(string projectPath, string assetPattern, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.FindMissingScripts, Target = assetPattern },
            ct: ct
        );

    public Task<ToolExecutionResult> ShowAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Show, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> SearchAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Search, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> ToJsonAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ToJson, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> FromJsonOverwriteAsync(string projectPath, string query, string json, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.FromJsonOverwrite, Target = query, Snippet = json },
            ct: ct
        );

    public Task<ToolExecutionResult> SaveScenesAsync(string projectPath, string? scenePath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.SaveScenes, Target = scenePath },
            ct: ct
        );

    public Task<ToolExecutionResult> DiscardScenesAsync(string projectPath, string? scenePath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.DiscardScenes, Target = scenePath },
            ct: ct
        );

    public async Task<ToolExecutionResult> RefreshAssetDatabaseAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        return await EnqueueAsync(
            projectPath: normalizedProjectPath,
            command: new() { CommandType = BridgeCommandTypes.RefreshAssetDatabase },
            ct: ct
        );
    }

    public async Task<ToolExecutionResult> ReimportAssetsAsync(string projectPath, string query, CT ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        return await EnqueueAsync(
            projectPath: normalizedProjectPath,
            command: new() { CommandType = BridgeCommandTypes.ReimportAssets, Target = query },
            ct: ct
        );
    }
}
