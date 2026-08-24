#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Conduit
{
    static partial class ScreenshotTool
    {
        const string OutputDirectoryName = "screenshot";
        const int DefaultRenderWidth = 1280;
        const int DefaultRenderHeight = 720;
        const float BoundsSizeEpsilon = 0.01f;

#if !MODULE_IMAGECONVERSION || !MODULE_SCREENCAPTURE
        internal static string ModuleUnavailableDiagnostic
        {
            get
            {
#if !MODULE_IMAGECONVERSION && !MODULE_SCREENCAPTURE
                return BuildModuleUnavailableDiagnostic(false, false);
#elif !MODULE_IMAGECONVERSION
                return BuildModuleUnavailableDiagnostic(false, true);
#elif !MODULE_SCREENCAPTURE
                return BuildModuleUnavailableDiagnostic(true, false);
#else
                return string.Empty;
#endif
            }
        }
#endif

        internal static string BuildModuleUnavailableDiagnostic(
            bool imageConversionEnabled,
            bool screenCaptureEnabled
        )
            => (imageConversionEnabled, screenCaptureEnabled) switch
            {
                (false, false) =>
                    "ERROR: Unity built-in modules `com.unity.modules.imageconversion` and " +
                    "`com.unity.modules.screencapture` are not enabled in this project. " +
                    "Ask the user for permission to enable the modules so that the `screenshot` tool can be used.",
                (false, true) =>
                    "ERROR: Unity built-in module `com.unity.modules.imageconversion` is not enabled in this project. " +
                    "Ask the user for permission to enable the module so that the `screenshot` tool can be used.",
                (true, false) =>
                    "ERROR: Unity built-in module `com.unity.modules.screencapture` is not enabled in this project. " +
                    "Ask the user for permission to enable the module so that the `screenshot` tool can be used.",
                _ => string.Empty,
            };

        internal static async Task<string> CaptureAsync(string? target)
        {
            var normalizedTarget = target?.Trim() ?? "";
            if (normalizedTarget.Length == 0)
                throw new InvalidOperationException("Screenshot target was empty.");

            if (string.Equals(normalizedTarget, "editor", StringComparison.OrdinalIgnoreCase))
                return await CaptureEditorAsync();

            if (string.Equals(normalizedTarget, "game_view", StringComparison.OrdinalIgnoreCase))
                return await CaptureGameViewAsync();

            if (string.Equals(normalizedTarget, "scene_view", StringComparison.OrdinalIgnoreCase))
                return await CaptureSceneViewAsync();

            if (normalizedTarget.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
                return await CaptureEditorWindowTargetAsync(normalizedTarget);

            if (ConduitAssetPathUtility.TryResolveAssetPath(normalizedTarget, out var assetPath)
                && assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return await CaptureSceneAssetAsync(assetPath);

            return await CaptureResolvedTargetAsync(normalizedTarget);
        }

        static async Task<string> CaptureResolvedTargetAsync(string target)
            => ConduitSearchUtility.Resolve(target) switch
            {
                { Count: 0 }           => $"No matches for '{target}'.",
                { Count: 1 } matches   => await CaptureResolvedMatchAsync(matches[0]),
                { Count: > 1 } matches => ConduitSearchUtility.FormatMatches(matches, includeHint: true),
            };

        static async Task<string> CaptureResolvedMatchAsync(ResolvedObjectMatch match)
        {
            var target = match.RequireTarget();
            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(assetPath) && assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                return await CaptureSceneAssetAsync(assetPath);

            if (TryGetSceneCamera(target, out var camera))
                return await CaptureCameraAsync(camera, match.Name);

            if (TryGetPreviewSource(target, out var previewSource))
                return await CaptureGameObjectPreviewAsync(previewSource, match.Name);

            if (EditorUtility.IsPersistent(target))
                return await CaptureAssetPreviewAsync(target, match.Name);

            throw new InvalidOperationException($"Target '{match.Name}' could not be rendered as a screenshot.");
        }

        static async Task<string> CaptureEditorAsync()
        {
            EnsureCanRenderScreenshot("editor");
            var sources = await EditorCaptureSource.CreateEditorSourcesAsync();
            using var pooledOutputs = ConduitPool.GetPooledList<ScreenshotOutputPath>(out var outputs);
            using var pooledResults = ConduitPool.GetPooledList<string>(out var results);
            if (outputs.Capacity < sources.Length)
                outputs.Capacity = sources.Length;
            if (results.Capacity < sources.Length)
                results.Capacity = sources.Length;
            try
            {
                foreach (var source in sources)
                {
                    var output = await SaveLiveEditorSourceAsync(source, source.Target);
                    outputs.Add(output);
                    results.Add($"{output.Prefix} image captured: {output.RelativePath}");
                }

                return string.Join("\n", results);
            }
            catch
            {
                // a multi-window request is atomic so failures never leave unreported partial captures
                foreach (var output in outputs)
                    File.Delete(output.AbsolutePath);

                throw;
            }
            finally
            {
                foreach (var source in sources)
                    source.Dispose();
            }
        }

        static async Task<string> CaptureEditorWindowTargetAsync(string target)
        {
            EnsureCanRenderScreenshot(target);
            if (string.IsNullOrWhiteSpace(target["window:".Length..].Trim()))
                throw new InvalidOperationException("Editor window screenshot target was empty.");

            var initialState = EditorCaptureSource.EditorWindowState.Capture();
            return ConduitSearchUtility.Resolve(target) switch
            {
                { Count: 0 }           => $"No matches for '{target}'.",
                { Count: 1 } matches   => await CaptureMatchAsync(matches[0]),
                { Count: > 1 } matches => ConduitSearchUtility.FormatMatches(matches, includeHint: true),
            };

            async Task<string> CaptureMatchAsync(ResolvedObjectMatch match)
            {
                if (match.Target is not EditorWindow window)
                    throw new InvalidOperationException($"Target '{match.Name}' is not an editor window.");

                var displayName = ConduitSearchUtility.GetEditorWindowDisplayName(window);
                return await CaptureLiveEditorSourceAsync(
                    await EditorCaptureSource.CreateWindowAsync(
                        window,
                        displayName,
                        initialState
                    ),
                    displayName
                );
            }
        }

        static async Task<string> CaptureGameViewAsync()
        {
            EnsureCanRenderScreenshot("game_view");
            return await CaptureLiveEditorSourceAsync("game_view", "game_view");
        }

        static async Task<string> CaptureSceneViewAsync()
        {
            EnsureCanRenderScreenshot("scene_view");
            return await CaptureLiveEditorSourceAsync("scene_view", "scene_view");
        }

        static async Task<string> CaptureSceneAssetAsync(string sceneAssetPath)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Scene asset screenshots are only supported in edit mode.");

            EnsureCanRenderScreenshot(Path.GetFileNameWithoutExtension(sceneAssetPath));
            var previewScene = EditorSceneManager.OpenPreviewScene(sceneAssetPath);
            try
            {
                await EditorCaptureSource.WaitForNextEditorUpdateAsync();
                if (!TryCalculateSceneBounds(previewScene, out var bounds))
                    throw new InvalidOperationException($"Scene '{sceneAssetPath}' has no visible renderer bounds to capture.");

                return await CaptureSceneBoundsAsync(
                    previewScene,
                    bounds,
                    Path.GetFileNameWithoutExtension(sceneAssetPath),
                    topDown: true
                );
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

    }
}
