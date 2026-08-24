#nullable enable

namespace Conduit
{
    static class BridgeCommandTypes
    {
        public const string Help = "help";
        public const string Restart = "restart";
        public const string Status = "status";
        public const string PlayMode = "playmode";
        public const string EditMode = "editmode";
        public const string Screenshot = "screenshot";
        public const string Record = "record";
        public const string GetDependencies = "get_dependencies";
        public const string FindReferencesTo = "find_references_to";
        public const string FindMissingScripts = "find_missing_scripts";
        public const string Show = "show";
        public const string Search = "search";
        public const string ToJson = "to_json";
        public const string FromJsonOverwrite = "from_json_overwrite";
        public const string SaveScenes = "save_scenes";
        public const string DiscardScenes = "discard_scenes";
        public const string RefreshAssetDatabase = "refresh_asset_database";
        public const string ReimportAssets = "reimport_assets";
        public const string ExecuteCode = "execute_code";
        public const string Detour = "detour";
        public const string ViewBurstAsm = "view_burst_asm";
        public const string Reflect = "reflect";
        public const string ProjectSettings = "project_settings";
        public const string RunTestsEditMode = "run_tests_editmode";
        public const string RunTestsPlayMode = "run_tests_playmode";
        public const string RunTestsPlayer = "run_tests_player";
        public const string ProfilerRecord = "profiler_record";
        public const string ProfilerOverview = "profiler_overview";
        public const string ProfilerBrowse = "profiler_browse";
        internal const string ProfilerHasMarker = "profiler_has_marker";
        internal const string CompilationReferences = "compilation_references";
        internal const string AssemblyBlob = "assembly_blob";
        internal const string QuitPlayer = "quit_player";
    }
}
