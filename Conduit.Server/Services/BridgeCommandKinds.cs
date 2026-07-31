namespace Conduit;

enum BridgeCommandKind : byte
{
    Unknown,
    Help,
    Restart,
    Status,
    PlayMode,
    EditMode,
    Screenshot,
    GetDependencies,
    FindReferencesTo,
    FindMissingScripts,
    Show,
    Search,
    ToJson,
    FromJsonOverwrite,
    SaveScenes,
    DiscardScenes,
    RefreshAssetDatabase,
    ReimportAssets,
    ExecuteCode,
    ViewBurstAsm,
    Reflect,
    RunTestsEditMode,
    RunTestsPlayMode,
    RunTestsPlayer,
    ProfilerRecord,
    ProfilerOverview,
    ProfilerBrowse,
    ProfilerHasMarker,
}

static class BridgeCommandKinds
{
    public static BridgeCommandKind Parse(string? commandType) =>
        commandType switch
        {
            BridgeCommandTypes.Help                 => BridgeCommandKind.Help,
            BridgeCommandTypes.Restart              => BridgeCommandKind.Restart,
            BridgeCommandTypes.Status               => BridgeCommandKind.Status,
            BridgeCommandTypes.PlayMode             => BridgeCommandKind.PlayMode,
            BridgeCommandTypes.EditMode             => BridgeCommandKind.EditMode,
            BridgeCommandTypes.Screenshot           => BridgeCommandKind.Screenshot,
            BridgeCommandTypes.GetDependencies      => BridgeCommandKind.GetDependencies,
            BridgeCommandTypes.FindReferencesTo     => BridgeCommandKind.FindReferencesTo,
            BridgeCommandTypes.FindMissingScripts   => BridgeCommandKind.FindMissingScripts,
            BridgeCommandTypes.Show                 => BridgeCommandKind.Show,
            BridgeCommandTypes.Search               => BridgeCommandKind.Search,
            BridgeCommandTypes.ToJson               => BridgeCommandKind.ToJson,
            BridgeCommandTypes.FromJsonOverwrite    => BridgeCommandKind.FromJsonOverwrite,
            BridgeCommandTypes.SaveScenes           => BridgeCommandKind.SaveScenes,
            BridgeCommandTypes.DiscardScenes        => BridgeCommandKind.DiscardScenes,
            BridgeCommandTypes.RefreshAssetDatabase => BridgeCommandKind.RefreshAssetDatabase,
            BridgeCommandTypes.ReimportAssets       => BridgeCommandKind.ReimportAssets,
            BridgeCommandTypes.ExecuteCode          => BridgeCommandKind.ExecuteCode,
            BridgeCommandTypes.ViewBurstAsm         => BridgeCommandKind.ViewBurstAsm,
            BridgeCommandTypes.Reflect              => BridgeCommandKind.Reflect,
            BridgeCommandTypes.RunTestsEditMode     => BridgeCommandKind.RunTestsEditMode,
            BridgeCommandTypes.RunTestsPlayMode     => BridgeCommandKind.RunTestsPlayMode,
            BridgeCommandTypes.RunTestsPlayer       => BridgeCommandKind.RunTestsPlayer,
            BridgeCommandTypes.ProfilerRecord       => BridgeCommandKind.ProfilerRecord,
            BridgeCommandTypes.ProfilerOverview     => BridgeCommandKind.ProfilerOverview,
            BridgeCommandTypes.ProfilerBrowse       => BridgeCommandKind.ProfilerBrowse,
            BridgeCommandTypes.ProfilerHasMarker    => BridgeCommandKind.ProfilerHasMarker,
            _                                       => BridgeCommandKind.Unknown,
        };

    public static bool IsTest(BridgeCommandKind commandKind) =>
        commandKind is BridgeCommandKind.RunTestsEditMode or BridgeCommandKind.RunTestsPlayMode or BridgeCommandKind.RunTestsPlayer;

    public static bool IsAssetImport(BridgeCommandKind commandKind) =>
        commandKind is BridgeCommandKind.RefreshAssetDatabase or BridgeCommandKind.ReimportAssets;

    public static bool IsProfiler(BridgeCommandKind commandKind) =>
        commandKind is BridgeCommandKind.ProfilerRecord
            or BridgeCommandKind.ProfilerOverview
            or BridgeCommandKind.ProfilerBrowse;
}
