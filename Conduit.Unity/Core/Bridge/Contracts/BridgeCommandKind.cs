#nullable enable

namespace Conduit
{
    enum BridgeCommandKind : byte
    {
        Unknown,
        Help,
        Restart,
        Status,
        PlayMode,
        EditMode,
        Screenshot,
        Record,
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
        Detour,
        ViewBurstAsm,
        Reflect,
        ProjectSettings,
        RunTestsEditMode,
        RunTestsPlayMode,
        RunTestsPlayer,
        ProfilerRecord,
        ProfilerOverview,
        ProfilerBrowse,
        ProfilerHasMarker,
        CompilationReferences,
        AssemblyBlob,
        QuitPlayer,
    }
}
