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
        string asset,
        UnityProjectOperations operations,
        CancellationToken ct
    ) => ToPlainTextToolResponseAsync(operations.ShowAsync(projectPath, asset, ct));

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
        """Triggers AssetDatabase.Refresh for the project and waits for the editor to become stable again"""
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

    static async Task<string> ToPlainTextToolResponseAsync(Task<ToolExecutionResult> resultTask)
        => ToolResponseFormatter.Format(await resultTask);
}
