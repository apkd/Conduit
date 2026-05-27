using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using ModelContextProtocol.Server;
using Conduit;
using CMD = Conduit.BridgeCommandTypes;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
[McpServerToolType]
[SuppressMessage("ReSharper", "RawStringCanBeSimplified")]
public sealed class UnityTools
{
    [McpServerTool(Name = CMD.Status)]
    [Description(
        """Checks whether a Unity project is reachable through the bridge and returns the project status or failure diagnostics"""
    )]
    public static Task<string> Status(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => operations.StatusAsync(projectPath, ct);

    [McpServerTool(Name = CMD.Play)]
    [Description(
        """Toggles between play mode and edit mode, and returns the mode that Unity entered"""
    )]
    public static Task<string> Play(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.PlayAsync(projectPath, ct));

    [McpServerTool(Name = CMD.Screenshot)]
    [Description(
        """
        Captures an image and saves it into Temp/screenshot.
        Supported targets include: editor, game_view, scene_view, a scene camera eid, an object eid/path/guid for preview capture,
        or a scene path/guid for top-down scene capture.
        Useful for debugging and validation. Always use the view_image tool to view the captured image.
        """
    )]
    public static Task<string> Screenshot(
        [Description("Project path")] string projectPath,
        [Description("Capture target. Examples: editor, game_view, scene_view, eid:12345, Assets/Foo.prefab, /Main Camera")]
        string target,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ScreenshotAsync(projectPath, target, ct));

    [McpServerTool(Name = "restart")]
    [Description(
        """
        Starts or restarts the Unity editor.
        Never kill the Unity process manually - simply use the `restart` tool to recover from any error state.
        """
    )]
    public static Task<string> Restart(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.RestartAsync(projectPath, ct));

    [McpServerTool(Name = "help")]
    [Description(
        $"""
         Returns additional help for finding objects with {CMD.Search}, {CMD.Show}, {CMD.ToJson}, and {CMD.FromJsonOverwrite}.
         Run this command once to find out how to efficiently search for objects.
         """
    )]
    public static string Help(UnityProjectRegistry projectRegistry)
        => HelpTool.GetHelpString(projectRegistry.GetLatestUnityVersion());

    [McpServerTool(Name = CMD.GetDependencies)]
    [Description(
        """
        Lists the assets that this asset directly references. Answers 'what does this use?'
        """
    )]
    public static Task<string> GetDependencies(
        [Description("Project path")] string projectPath,
        [Description("Asset path or a GUID string")]
        string asset,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.GetDependenciesAsync(projectPath, asset, ct));

    [McpServerTool(Name = CMD.FindReferencesTo)]
    [Description(
        """Lists assets that directly reference the specified asset. """
    )]
    public static Task<string> FindReferencesTo(
        [Description("Project path")] string projectPath,
        [Description("Asset path or a GUID string")]
        string asset,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("The object reference graph is cached after first call. Use this to invalidate the cache; usually unnecessary, unless you modified the assets")]
        bool rebuildCache = false
    ) => ToPlainTextToolResponseAsync(operations.FindReferencesToAsync(projectPath, asset, rebuildCache, ct));

    [McpServerTool(Name = CMD.FindMissingScripts)]
    [Description(
        """Finds missing MonoBehaviour scripts in one or more scenes or prefabs"""
    )]
    public static Task<string> FindMissingScripts(
        [Description("Project path")] string projectPath,
        [Description("Scene or prefab path, directory, GUID, or wildcard pattern to scan. Examples: Assets/Scenes, Assets/**/*.prefab")]
        string assetPattern,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.FindMissingScriptsAsync(projectPath, assetPattern, ct));

    [McpServerTool(Name = CMD.Show)]
    [Description(
        $"""
         Prints a compact, readable description of a Unity object. Displays both serialized and private fields.
         For GameObjects, scenes and prefabs, includes a compact hierarchy tree. For assets, includes sub-assets.
         Use this for inspecting runtime objects, prefabs, scenes, components, ScriptableObjects and any other assets.
         Useful for debugging and general exploration.
         """
    )]
    public static Task<string> Show(
        [Description("Project path")] string projectPath,
        [Description("Entity ID, asset path, hierarchy path, or Unity search query. Examples: eid:12345, Assets/Foo.prefab, /Root/Child")]
        string query,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ShowAsync(projectPath, query, ct));

    [McpServerTool(Name = CMD.Search)]
    [Description(
        $"""
        Universal Unity search tool that supports assets, prefabs, scene GameObjects, tests, and more.
        Prints each found object's name, asset path and ID.
        After you find an object, you can use its ID with other commands, such as {CMD.Show}, {CMD.ToJson}, etc.

        Use the help command for more search tips.
        """
    )]
    public static Task<string> Search(
        [Description("Project path")] string projectPath,
        [Description("The search query. Use the help command to learn more. Examples: t:prefab camera, ext=mat, +fuzzy PlayerPrefab")]
        string query,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.SearchAsync(projectPath, query, ct));

    [McpServerTool(Name = CMD.ToJson)]
    [Description(
        $"""Reads a resolved Unity object and returns its JSON representation. (Combine with: {CMD.FromJsonOverwrite}.)"""
    )]
    public static Task<string> ToJson(
        [Description("Project path")] string projectPath,
        [Description("Entity ID, asset path, hierarchy path, or Unity search query")]
        string query,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ToJsonAsync(projectPath, query, ct));

    [McpServerTool(Name = CMD.FromJsonOverwrite)]
    [Description(
        """
        Resolves a single Unity object, applies EditorJsonUtility.FromJsonOverwrite to it, persists asset changes,
        leaves scene-object changes dirty for save_scenes/discard_scenes, and returns the changed serialized property paths
        """
    )]
    public static Task<string> FromJsonOverwrite(
        [Description("Project path")] string projectPath,
        [Description("Entity ID, asset path, hierarchy path, or Unity search query")]
        string query,
        [Description("JSON patch payload. Fields omitted from the payload remain unchanged")]
        string json,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.FromJsonOverwriteAsync(projectPath, query, json, ct));

    [McpServerTool(Name = CMD.SaveScenes)]
    [Description(
        """Saves dirty open scenes. When no specific scene path is provided, saves all dirty open scenes"""
    )]
    public static Task<string> SaveScenes(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional exact open scene path to save. Omit to save all dirty open scenes")]
        string? scenePath = null
    ) => ToPlainTextToolResponseAsync(operations.SaveScenesAsync(projectPath, scenePath, ct));

    [McpServerTool(Name = CMD.DiscardScenes)]
    [Description(
        """
        Discards changes in dirty open scenes. When specific no scene path is provided, discards all dirty open scenes;
        untitled/temp scenes are discarded
        """
    )]
    public static Task<string> DiscardScenes(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional exact open scene path to discard. Omit to discard all dirty open scenes")]
        string? scenePath = null
    ) => ToPlainTextToolResponseAsync(operations.DiscardScenesAsync(projectPath, scenePath, ct));

    [McpServerTool(Name = CMD.RefreshAssetDatabase)]
    [Description(
        """
        Triggers AssetDatabase.Refresh for the project and waits for the editor to become stable again.
        Never call AssetDatabase.Refresh manually. ALWAYS use refresh_asset_database instead.
        """
    )]
    public static Task<string> RefreshAssetDatabase(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.RefreshAssetDatabaseAsync(projectPath, ct));

    [McpServerTool(Name = CMD.ExecuteCode)]
    [Description(
        """
        Immediately compiles and runs a one-off C# snippet inside the Unity editor. Works in edit mode and in play mode.
        Supports top-level statements, local functions, leading using directives, leading type declarations, and leading static fields.
        The generated snippet already imports System, generic collections, IO, Linq, Tasks, UnityEditor, and UnityEngine; skip these namespaces.
        You can also skip whitespace and other tokens that don't impact execution. Prefer extremely terse code; single-letter variable names, etc.
        Useful for testing and debugging, prototyping code, validating assumptions, and even making modifications to the project.
        """
    )]
    public static Task<string> ExecuteCode(
        [Description("Project path")] string projectPath,
        [Description("C# code to execute")] string snippet,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ExecuteCodeAsync(projectPath, snippet, ct));

    [McpServerTool(Name = CMD.ViewBurstAsm)]
    [Description(
        """
        Shows low-level optimization statistics and assembly for a Burst compilation target.
        Use this for job optimization and for validating and debugging Burst compilation.
        """
    )]
    public static Task<string> ViewBurstAsm(
        [Description("Project path")] string projectPath,
        [Description("Burst compilation target (job/method) name or partial name")]
        string target,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ViewBurstAsmAsync(projectPath, target, ct));

    [McpServerTool(Name = CMD.Reflect)]
    [Description(
        """
        Searches loaded assemblies for C# types and members.
        You can search for types (based on either a `type` name query or a contained `member` name query)
        or members (locally if you include the containing `type` name query or globally if you provide a `member` name query instead).
        """
    )]
    public static Task<string> Reflect(
        [Description("Project path")] string projectPath,
        [Description("Search mode: types/classes/structs/enums/interfaces/delegates or members/fields/properties/methods/constructors")]
        string mode,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Type name query. Use full type name or 'Full.Type.Name, AssemblyName' to disambiguate.")]
        string? type = null,
        [Description("Member name query")]
        string? member = null
    ) => ToPlainTextToolResponseAsync(operations.ReflectAsync(projectPath, mode, type, member, ct));

    [McpServerTool(Name = CMD.RunTestsEditMode)]
    [Description("Runs the edit mode test suite")]
    public static Task<string> RunTestsEditMode(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional glob-like filter matched against full test names")]
        string? testFilter = null
    ) => ToPlainTextToolResponseAsync(operations.RunTestsEditModeAsync(projectPath, testFilter, ct));

    [McpServerTool(Name = CMD.RunTestsPlayMode)]
    [Description("Runs the play mode test suite")]
    public static Task<string> RunTestsPlayMode(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional glob-like filter matched against full test names")]
        string? testFilter = null
    ) => ToPlainTextToolResponseAsync(operations.RunTestsPlayModeAsync(projectPath, testFilter, ct));

    [McpServerTool(Name = CMD.RunTestsPlayer)]
    [Description("Builds the Unity player and runs the test suite using the current build target and settings")]
    public static Task<string> RunTestsPlayer(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional glob-like filter matched against full test names")]
        string? testFilter = null
    ) => ToPlainTextToolResponseAsync(operations.RunTestsPlayerAsync(projectPath, testFilter, ct));

    [McpServerTool(Name = CMD.ProfilerRecord)]
    [Description(
        """
        Captures Unity Profiler frames or saves/loads profiler capture files.
        """
    )]
    public static Task<string> ProfilerRecord(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Action to perform: capture, save, load, or list")]
        ProfilerRecordAction action = ProfilerRecordAction.Capture,
        [Description("Number of completed frames to capture when action is capture (max 600).")]
        int frames = 120,
        [Description("Seconds to wait before clearing profiler history and capturing frames. Applies only to capture.")]
        double delaySeconds = 1,
        [Description("Capture target. Use play_mode or edit_mode.")]
        string target = "play_mode",
        [Description("Capture file name or path.")]
        string? fileName = null
    ) => ToPlainTextToolResponseAsync(operations.ProfilerRecordAsync(projectPath, action, frames, delaySeconds, target, fileName, ct));

    [McpServerTool(Name = CMD.ProfilerOverview)]
    [Description(
        """
        Summarizes Unity Profiler frames and main-thread hot samples.
        """
    )]
    public static Task<string> ProfilerOverview(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Sort metric: cpu_ms or gc_kb")]
        ProfilerOverviewMode mode = ProfilerOverviewMode.CpuMs,
        [Description("Available-frame ordinal range. Examples: 0..^1, ^120..^1, 10..50.")]
        string frameRange = "0..^1"
    ) => ToPlainTextToolResponseAsync(operations.ProfilerOverviewAsync(projectPath, mode, frameRange, ct));

    [McpServerTool(Name = CMD.ProfilerBrowse)]
    [Description(
        """
        Browses the sample hierarchy of a captured frame in the Unity Profiler.
        """
    )]
    public static Task<string> ProfilerBrowse(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Frame selector: selected, latest, or an available-frame ordinal.")]
        string frame = "selected",
        [Description("Thread selector: main, render, or worker<N>.")]
        string thread = "main",
        [Description("Root selector: empty root, an output id, slash-separated path, or exact marker/sample name.")]
        string root = "",
        [Description("Number of hierarchy levels to print. Clamped to 1..32.")]
        int depth = 3,
        [Description("Sort metric: total_ms, self_ms, gc_bytes, or calls.")]
        ProfilerBrowseSort sort = ProfilerBrowseSort.TotalMs,
        [Description("Maximum number of rows to print. Clamped to 1..200.")]
        int limit = 50,
        [Description("Exclude rows that are insignificant for the selected sort metric.")]
        bool onlyNonTrivial = true
    ) => ToPlainTextToolResponseAsync(
        operations.ProfilerBrowseAsync(projectPath, frame, thread, root, depth, sort, limit, onlyNonTrivial, ct)
    );

    static async Task<string> ToPlainTextToolResponseAsync(Task<ToolExecutionResult> resultTask)
        => ToolResponseFormatter.Format(await resultTask);
}
