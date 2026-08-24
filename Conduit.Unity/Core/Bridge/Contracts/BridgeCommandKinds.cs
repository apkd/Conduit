#nullable enable

namespace Conduit
{
    static class BridgeCommandKinds
    {
        public static BridgeCommandKind Parse(string? commandType)
            => commandType switch
            {
                BridgeCommandTypes.Help                  => BridgeCommandKind.Help,
                BridgeCommandTypes.Restart               => BridgeCommandKind.Restart,
                BridgeCommandTypes.Status                => BridgeCommandKind.Status,
                BridgeCommandTypes.PlayMode              => BridgeCommandKind.PlayMode,
                BridgeCommandTypes.EditMode              => BridgeCommandKind.EditMode,
                BridgeCommandTypes.Screenshot            => BridgeCommandKind.Screenshot,
                BridgeCommandTypes.Record                => BridgeCommandKind.Record,
                BridgeCommandTypes.GetDependencies       => BridgeCommandKind.GetDependencies,
                BridgeCommandTypes.FindReferencesTo      => BridgeCommandKind.FindReferencesTo,
                BridgeCommandTypes.FindMissingScripts    => BridgeCommandKind.FindMissingScripts,
                BridgeCommandTypes.Show                  => BridgeCommandKind.Show,
                BridgeCommandTypes.Search                => BridgeCommandKind.Search,
                BridgeCommandTypes.ToJson                => BridgeCommandKind.ToJson,
                BridgeCommandTypes.FromJsonOverwrite     => BridgeCommandKind.FromJsonOverwrite,
                BridgeCommandTypes.SaveScenes            => BridgeCommandKind.SaveScenes,
                BridgeCommandTypes.DiscardScenes         => BridgeCommandKind.DiscardScenes,
                BridgeCommandTypes.RefreshAssetDatabase  => BridgeCommandKind.RefreshAssetDatabase,
                BridgeCommandTypes.ReimportAssets        => BridgeCommandKind.ReimportAssets,
                BridgeCommandTypes.ExecuteCode           => BridgeCommandKind.ExecuteCode,
                BridgeCommandTypes.Detour                => BridgeCommandKind.Detour,
                BridgeCommandTypes.ViewBurstAsm          => BridgeCommandKind.ViewBurstAsm,
                BridgeCommandTypes.Reflect               => BridgeCommandKind.Reflect,
                BridgeCommandTypes.ProjectSettings       => BridgeCommandKind.ProjectSettings,
                BridgeCommandTypes.RunTestsEditMode      => BridgeCommandKind.RunTestsEditMode,
                BridgeCommandTypes.RunTestsPlayMode      => BridgeCommandKind.RunTestsPlayMode,
                BridgeCommandTypes.RunTestsPlayer        => BridgeCommandKind.RunTestsPlayer,
                BridgeCommandTypes.ProfilerRecord        => BridgeCommandKind.ProfilerRecord,
                BridgeCommandTypes.ProfilerOverview      => BridgeCommandKind.ProfilerOverview,
                BridgeCommandTypes.ProfilerBrowse        => BridgeCommandKind.ProfilerBrowse,
                BridgeCommandTypes.ProfilerHasMarker     => BridgeCommandKind.ProfilerHasMarker,
                BridgeCommandTypes.CompilationReferences => BridgeCommandKind.CompilationReferences,
                BridgeCommandTypes.AssemblyBlob          => BridgeCommandKind.AssemblyBlob,
                BridgeCommandTypes.QuitPlayer            => BridgeCommandKind.QuitPlayer,
                _                                        => BridgeCommandKind.Unknown,
            };

        public static bool IsTest(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.RunTestsEditMode
                or BridgeCommandKind.RunTestsPlayMode
                or BridgeCommandKind.RunTestsPlayer;

        public static bool IsAssetImport(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.RefreshAssetDatabase or BridgeCommandKind.ReimportAssets;

        public static bool IsEditorMode(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.PlayMode or BridgeCommandKind.EditMode;

        public static bool IsProfiler(BridgeCommandKind commandKind)
            => commandKind is BridgeCommandKind.ProfilerRecord
                or BridgeCommandKind.ProfilerOverview
                or BridgeCommandKind.ProfilerBrowse;

        public static bool SupportsCancellation(BridgeCommandKind commandKind)
            => IsTest(commandKind) || commandKind == BridgeCommandKind.Record;
    }
}
