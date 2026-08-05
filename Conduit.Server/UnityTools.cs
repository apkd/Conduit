using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol.Server;
using Conduit;
using CMD = Conduit.BridgeCommandTypes;

[McpServerToolType]
[SuppressMessage("ReSharper", "RawStringCanBeSimplified")]
public sealed class UnityTools
{
    [McpServerTool(Name = CMD.Status, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        """Checks whether a Unity project is reachable through the bridge and returns the project status or failure diagnostics"""
    )]
    public static Task<string> Status(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => operations.StatusAsync(projectPath, ct);

    [McpServerTool(Name = CMD.PlayMode, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        """Enters play mode, and reports when Unity is already in play mode"""
    )]
    public static Task<string> PlayMode(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.EnterPlayModeAsync(projectPath, ct));

    [McpServerTool(Name = CMD.EditMode, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        """Enters edit mode, and reports when Unity is already in edit mode"""
    )]
    public static Task<string> EditMode(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.EnterEditModeAsync(projectPath, ct));

    [McpServerTool(Name = CMD.Screenshot, ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        """
        Captures an image and saves it into Temp/screenshot.
        Supported targets include: game_view, scene_view, window:<name>, a scene camera eid, an object eid/path/guid for preview capture,
        or a scene path/guid for top-down scene capture.
        Useful for debugging and validation. Always use the view_image tool to view the captured image.
        """
    )]
    public static Task<string> Screenshot(
        [Description("Project path")] string projectPath,
        [Description("Capture target. Examples: game_view, scene_view, window:Inspector, eid:12345, Assets/Foo.prefab, /Main Camera")]
        string target,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ScreenshotAsync(projectPath, target, ct));

    [McpServerTool(Name = CMD.Record, ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        """
        Records a Unity editor view through FFmpeg. The first call starts recording and returns immediately; call again while recording to wait for the result.
        Supported targets: game_view, scene_view, window:<name>. Supported formats: auto, x264, x265, x264_hw, x265_hw, webm, gif.
        Output is stored under Library/Recordings. FFmpeg must be available on PATH.
        """
    )]
    public static Task<string> Record(
        [Description("Project path")] string projectPath,
        [Description("Capture target: game_view, scene_view, or window:<name>")] string target,
        [Description("Output duration in seconds, greater than zero and at most 1800")] float durationSeconds,
        [Description("Use Time.captureDeltaTime for deterministic, smooth game-time capture. Requires unpaused play mode.")]
        bool adjustDeltaTime,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Output frame rate from 1 through 240")] int frameRate = 30,
        [Description("GPU capture scale from 0.1 through 1.0")] float resolution_scale = 0.5f,
        [Description("Encoding format: auto, x264, x265, x264_hw, x265_hw, webm, or gif")]
        string format = "auto",
        [Description("Encoder quality value. Lower values generally preserve more detail; ignored for GIF.")] int crf = 23
    ) => ToPlainTextToolResponseAsync(
        operations.RecordAsync(
            projectPath,
            target,
            durationSeconds,
            adjustDeltaTime,
            frameRate,
            resolution_scale,
            format,
            crf,
            ct
        )
    );

    [McpServerTool(Name = "restart", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
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

    [McpServerTool(Name = "help", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        $"""
         Returns usage guidance for object search, execute_code helpers, detour, and project_settings.
         Run this command once to see query and tool examples.
         """
    )]
    public static Task<string> Help(
        UnityProjectRegistry projectRegistry,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional project path or player selector")]
        string? projectPath = null)
    {
        if (PlayerSelector.TryParse(BridgeTarget.Normalize(projectPath), out _))
            return ToPlainTextToolResponseAsync(operations.HelpAsync(projectPath!, ct));

        return Task.FromResult(HelpTool.GetHelpString(projectRegistry.GetLatestUnityVersion()));
    }

    [McpServerTool(Name = CMD.GetDependencies, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.FindReferencesTo, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.FindMissingScripts, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.Show, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.Search, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.ToJson, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.FromJsonOverwrite, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.SaveScenes, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.DiscardScenes, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.RefreshAssetDatabase, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description(
        """
        Triggers AssetDatabase.Refresh for the project and waits for the editor to become stable again.
        Preferred over `reimport_assets`. Use this during normal iteration to refresh editor state after making changes.
        Never call AssetDatabase.Refresh manually. ALWAYS use refresh_asset_database instead.
        """
    )]
    public static Task<string> RefreshAssetDatabase(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.RefreshAssetDatabaseAsync(projectPath, ct));

    [McpServerTool(Name = CMD.ReimportAssets, ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description(
        """
        Resolves all assets matching the query, forces synchronous reimport, and waits for the editor to become stable again.
        Do not use unless you actually need to force reimport an asset. Usually prefer `refresh_asset_database` after making changes.
        """
    )]
    public static Task<string> ReimportAssets(
        [Description("Project path")] string projectPath,
        [Description("Entity ID, asset path, GUID, or Unity search query. All matching assets are reimported.")]
        string query,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ReimportAssetsAsync(projectPath, query, ct));

    [McpServerTool(Name = CMD.ExecuteCode, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        """
        Immediately compiles and runs a one-off C# snippet inside the selected Unity Editor or Development player.
        Pass a snippet filename returned by an earlier invocation, such as `7.cs`, to run it again without recompiling.
        Supports top-level statements, local functions, leading using directives, leading type declarations, and leading static fields.
        You can optionally return a result value to print it in the response.
        The generated snippet already imports System, System.Collections.Generic, System.IO, Linq, Tasks, and UnityEngine; Editor snippets also import UnityEditor.
        It also supports object search helpers; call Search<T>("query") or SearchMany<T>("query") to resolve Unity objects,
        supporting the same loaded-object queries as the `search` tool (use the help command for more search tips), as well as reflection helpers based on the `reflect` tool;
        for example, `Type t = Reflect.Type("Camera")`, `MethodInfo[] methods = Reflect.Methods(type: "UnsafeUtility")`.
        You can also skip whitespace and other tokens that don't impact execution. Prefer extremely terse code; single-letter variable names, etc.
        Useful for testing and debugging, prototyping code, validating assumptions, and even making modifications to the project.
        """
    )]
    public static Task<string> ExecuteCode(
        [Description("Project path or player selector")] string projectPath,
        [Description("C# code to execute, or a prior script filename such as 7.cs to run again")]
        string snippet,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ExecuteCodeAsync(projectPath, snippet, ct));

    [McpServerTool(Name = CMD.Detour, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description(
        """
        Replaces a managed C# method implementation at runtime.
        The replacement is a method-body snippet whose instance receiver is `@this` and parameters are `arg0`, `arg1`, etc.
        Pass exactly `test` to inspect support and print the replacement signature, or `restore` to revert changes.
        Supports managed, non-generic static and instance methods, including ref/ref readonly returns, Span, pointers, and function pointers.
        Constructors, abstract/runtime/native methods, varargs, and signatures that C# cannot represent exactly are unsupported.
        Runtime patching requires Windows or Linux x64 Unity Mono.
        Use this for testing bugfixes, prototyping features, and any other scenario where changing a method's behavior is useful.
        Use the `reflect` tool with `mode=methods` to find methods that you can replace.
        Methods ending in `// detour-incompatible` cannot currently be replaced.
        """
    )]
    public static Task<string> Detour(
        [Description("Project path or player selector")] string projectPath,
        [Description("Target method name, including type name, or canonical selector returned by `test`")] string methodName,
        [Description("C# replacement body, `test`, `restore`, or a prior script filename such as 5.cs to apply again")]
        string replacementBody,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.DetourAsync(projectPath, methodName, replacementBody, ct));

    [McpServerTool(Name = CMD.ViewBurstAsm, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.Reflect, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        """
        Searches loaded assemblies for C# types and members.
        You can search for types (based on either a `type` name query or a contained `member` name query)
        or members (locally if you include the containing `type` name query or globally if you provide a `member` name query instead).
        Method declarations ending in `// detour-incompatible` cannot currently be replaced by the `detour` tool.
        """
    )]
    public static Task<string> Reflect(
        [Description("Project path or player selector")] string projectPath,
        [Description("Search mode: types/classes/structs/enums/interfaces/delegates or members/fields/properties/methods/constructors")]
        string mode,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Type name query. Use full type name or 'Full.Type.Name, AssemblyName' to disambiguate.")]
        string? type = null,
        [Description("Member name query")]
        string? member = null
    ) => ToPlainTextToolResponseAsync(operations.ReflectAsync(projectPath, mode, type, member, ct));

    [McpServerTool(Name = CMD.ProjectSettings, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description(
        """
        Searches, reads or changes project-level settings.
        Supports a wide array of providers: player settings, quality/graphics/render pipeline settings, input system, build profiles, etc.
        Get with an empty key lists setting groups; get with a group key lists every setting in that group.
        Scalar settings are simple C# values and compound values use JSON.
        """
    )]
    public static Task<string> ProjectSettings(
        [Description("Project path")] string projectPath,
        [Description("Exact or partial setting key")] string key,
        [Description("Operation: get, set, add_element, or remove_element")] string operation,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Setting value. Omit to pass null.")]
        string? value = null
    ) => ToPlainTextToolResponseAsync(operations.ProjectSettingsAsync(projectPath, key, operation, value, ct));

    [McpServerTool(Name = CMD.RunTestsEditMode, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Runs the edit mode test suite")]
    public static Task<string> RunTestsEditMode(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional glob-like filter matched against full test names")]
        string? testFilter = null,
        [Description("True starts the test run and returns immediately while tests continue asynchronously.")]
        bool @async = false
    ) => ToPlainTextToolResponseAsync(operations.RunTestsEditModeAsync(projectPath, testFilter, @async, ct));

    [McpServerTool(Name = CMD.RunTestsPlayMode, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Runs the play mode test suite")]
    public static Task<string> RunTestsPlayMode(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional glob-like filter matched against full test names")]
        string? testFilter = null,
        [Description("True starts the test run and returns immediately while tests continue asynchronously.")]
        bool @async = false
    ) => ToPlainTextToolResponseAsync(operations.RunTestsPlayModeAsync(projectPath, testFilter, @async, ct));

    [McpServerTool(Name = CMD.RunTestsPlayer, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Builds the Unity player and runs the test suite using the current build target and settings")]
    public static Task<string> RunTestsPlayer(
        [Description("Project path")] string projectPath,
        UnityProjectOperations operations,
        CancellationToken ct,
        [Description("Optional glob-like filter matched against full test names")]
        string? testFilter = null
    ) => ToPlainTextToolResponseAsync(operations.RunTestsPlayerAsync(projectPath, testFilter, ct));

    [McpServerTool(Name = CMD.ProfilerRecord, ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.ProfilerOverview, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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

    [McpServerTool(Name = CMD.ProfilerBrowse, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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
        [Description("Thread selector: main, render, all_workers, or worker<N>.")]
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
