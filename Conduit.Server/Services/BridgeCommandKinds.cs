namespace Conduit;

enum BridgeCommandKind : byte
{
    Unknown,
    Status,
    Play,
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
    ExecuteCode,
    ViewBurstAsm,
    RunTestsEditMode,
    RunTestsPlayMode,
    RunTestsPlayer,
}

static class BridgeCommandKinds
{
    public static BridgeCommandKind Parse(string? commandType) =>
        commandType switch
        {
            BridgeCommandTypes.Status               => BridgeCommandKind.Status,
            BridgeCommandTypes.Play                 => BridgeCommandKind.Play,
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
            BridgeCommandTypes.ExecuteCode          => BridgeCommandKind.ExecuteCode,
            BridgeCommandTypes.ViewBurstAsm         => BridgeCommandKind.ViewBurstAsm,
            BridgeCommandTypes.RunTestsEditMode     => BridgeCommandKind.RunTestsEditMode,
            BridgeCommandTypes.RunTestsPlayMode     => BridgeCommandKind.RunTestsPlayMode,
            BridgeCommandTypes.RunTestsPlayer       => BridgeCommandKind.RunTestsPlayer,
            _                                       => BridgeCommandKind.Unknown,
        };

    public static bool IsTest(BridgeCommandKind commandKind) =>
        commandKind is BridgeCommandKind.RunTestsEditMode or BridgeCommandKind.RunTestsPlayMode or BridgeCommandKind.RunTestsPlayer;
}
