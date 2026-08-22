#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class screenshot
    {
        const string OutputDirectoryName = "screenshot";
        const int DefaultRenderWidth = 1280;
        const int DefaultRenderHeight = 720;
        const float BoundsSizeEpsilon = 0.01f;

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

        public static async Task<string> CaptureAsync(string? target)
        {
            var normalizedTarget = target?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(normalizedTarget))
                throw new InvalidOperationException("Screenshot target was empty.");

            if (string.Equals(normalizedTarget, "editor", StringComparison.OrdinalIgnoreCase))
                return CaptureEditorWindow();

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
            var target = match.Target;
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

        static string CaptureEditorWindow()
            => throw new InvalidOperationException(
                "'editor' screenshots are not supported reliably. Unity only exposes screen-pixel capture for the full editor window, which depends on the editor being the frontmost OS window."
            );

        static async Task<string> CaptureEditorWindowTargetAsync(string target)
        {
            EnsureCanRenderScreenshot(target);
            if (string.IsNullOrWhiteSpace(target["window:".Length..].Trim()))
                throw new InvalidOperationException("Editor window screenshot target was empty.");

            var initialState = EditorCaptureSource.EditorWindowState.Capture();
            return ConduitSearchUtility.Resolve(target) switch
            {
                { Count: 0 }           => $"No matches for '{target}'.",
                { Count: 1 } matches   => matches[0].Target is EditorWindow window
                    ? await CaptureLiveEditorSourceAsync(
                        await EditorCaptureSource.CreateWindowAsync(
                            window,
                            ConduitSearchUtility.GetEditorWindowDisplayName(window),
                            initialState
                        ),
                        ConduitSearchUtility.GetEditorWindowDisplayName(window)
                    )
                    : throw new InvalidOperationException($"Target '{matches[0].Name}' is not an editor window."),
                { Count: > 1 } matches => ConduitSearchUtility.FormatMatches(matches, includeHint: true),
            };
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

        static async Task<string> CaptureCameraAsync(Camera sourceCamera, string prefix)
        {
            EnsureCanRenderScreenshot(prefix);
            var width = Mathf.Max(1, sourceCamera.pixelWidth);
            var height = Mathf.Max(1, sourceCamera.pixelHeight);
            if (width <= 1 || height <= 1)
                (width, height) = GetDefaultCaptureSize(sourceCamera.aspect);

            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTargetTexture = sourceCamera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                sourceCamera.targetTexture = renderTexture;
                sourceCamera.Render();
                RenderTexture.active = renderTexture;
                return await SaveRenderTextureAsync(renderTexture, prefix);
            }
            finally
            {
                RenderTexture.active = previousActive;
                sourceCamera.targetTexture = previousTargetTexture;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static async Task<string> CaptureGameObjectPreviewAsync(GameObject previewSource, string prefix)
        {
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject? previewInstance = null;
            try
            {
                previewInstance = Object.Instantiate(previewSource);
                previewInstance.hideFlags = HideFlags.HideAndDontSave;
                previewInstance.SetActive(true);
                SceneManager.MoveGameObjectToScene(previewInstance, previewScene);

                if (!TryCalculateRendererBounds(previewInstance, out var bounds))
                    throw new InvalidOperationException($"Target '{previewSource.name}' has no non-trivial renderer bounds to preview.");

                return await CaptureSceneBoundsAsync(previewScene, bounds, prefix, topDown: false);
            }
            finally
            {
                if (previewInstance != null)
                    Object.DestroyImmediate(previewInstance);

                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static async Task<string> CaptureAssetPreviewAsync(Object target, string prefix)
        {
            EnsureCanRenderScreenshot(prefix);
            var previewTexture = AssetPreview.GetAssetPreview(target);
            var deadlineUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (previewTexture == null
                   && IsLoadingAssetPreview(target)
                   && DateTime.UtcNow < deadlineUtc)
            {
                await Task.Delay(100);
                previewTexture = AssetPreview.GetAssetPreview(target);
            }

            previewTexture ??= AssetPreview.GetMiniThumbnail(target);
            if (previewTexture == null)
                throw new InvalidOperationException($"Unity could not generate a preview image for '{target.name}'.");

            return await SaveTextureAsync(previewTexture, prefix);
        }

        static bool IsLoadingAssetPreview(Object target)
        {
#if UNITY_6000_4_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview(target.GetEntityId());
#else
            return AssetPreview.IsLoadingAssetPreview(target.GetInstanceID());
#endif
        }

        static async Task<string> CaptureSceneBoundsAsync(Scene scene, Bounds bounds, string prefix, bool topDown)
        {
            EnsureCanRenderScreenshot(prefix);
            var (width, height) = GetDefaultCaptureSize(16f / 9f);
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            GameObject? cameraObject = null;
            GameObject? keyLightObject = null;
            GameObject? fillLightObject = null;
            var previousActive = RenderTexture.active;

            try
            {
                cameraObject = CreateSceneObject("__ConduitScreenshotCamera", scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.cameraType = CameraType.Preview;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new(0.18f, 0.18f, 0.18f, 1f);
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.targetTexture = renderTexture;
                camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);

                if (topDown)
                    ConfigureTopDownCamera(camera, bounds, (float)width / height);
                else
                    ConfigurePreviewCamera(camera, bounds);

                keyLightObject = CreateDirectionalLight(scene, "__ConduitScreenshotKeyLight", new(50f, 330f, 0f), 1.2f);
                fillLightObject = CreateDirectionalLight(scene, "__ConduitScreenshotFillLight", new(340f, 35f, 0f), 0.55f);

                camera.Render();
                RenderTexture.active = renderTexture;
                return await SaveRenderTextureAsync(renderTexture, prefix);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (cameraObject != null)
                    Object.DestroyImmediate(cameraObject);

                if (keyLightObject != null)
                    Object.DestroyImmediate(keyLightObject);

                if (fillLightObject != null)
                    Object.DestroyImmediate(fillLightObject);

                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static void ConfigureTopDownCamera(Camera camera, Bounds bounds, float aspect)
        {
            var orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / Mathf.Max(aspect, 0.01f));
            orthographicSize = Mathf.Max(orthographicSize * 1.15f, 1f);

            var elevation = Mathf.Max(bounds.size.y + orthographicSize * 2.5f, 10f);
            camera.orthographic = true;
            camera.orthographicSize = orthographicSize;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = elevation + bounds.extents.y * 4f + 100f;
            camera.transform.position = bounds.center + Vector3.up * elevation;
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        static void ConfigurePreviewCamera(Camera camera, Bounds bounds)
        {
            var viewDirection = new Vector3(-1f, 0.75f, -1f).normalized;
            var radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            const float fieldOfView = 30f;
            var distance = radius / Mathf.Sin(fieldOfView * 0.5f * Mathf.Deg2Rad);
            distance = Mathf.Max(distance * 1.15f, 2f);

            camera.orthographic = false;
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = distance + radius * 4f + 50f;
            camera.transform.position = bounds.center - viewDirection * distance;
            camera.transform.rotation = Quaternion.LookRotation(viewDirection, Vector3.up);
        }

        static GameObject CreateDirectionalLight(Scene scene, string name, Vector3 eulerAngles, float intensity)
        {
            var lightObject = CreateSceneObject(name, scene);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.color = Color.white;
            light.transform.rotation = Quaternion.Euler(eulerAngles);
            return lightObject;
        }

        static GameObject CreateSceneObject(string name, Scene scene)
        {
            var gameObject = new GameObject(name)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };

            SceneManager.MoveGameObjectToScene(gameObject, scene);
            return gameObject;
        }

        static bool TryGetSceneCamera(Object target, out Camera camera)
        {
            switch (target)
            {
                case Camera sceneCamera when !EditorUtility.IsPersistent(sceneCamera):
                {
                    camera = sceneCamera;
                    return true;
                }
                case GameObject gameObject when !EditorUtility.IsPersistent(gameObject) && gameObject.TryGetComponent(out Camera sceneCameraOnGameObject):
                {
                    camera = sceneCameraOnGameObject;
                    return true;
                }
                default:
                {
                    camera = null!;
                    return false;
                }
            }
        }

        static bool TryGetPreviewSource(Object target, out GameObject previewSource)
        {
            switch (target)
            {
                case GameObject gameObject:
                {
                    previewSource = gameObject;
                    return true;
                }
                case Component component:
                {
                    previewSource = component.gameObject;
                    return true;
                }
                default:
                {
                    previewSource = null!;
                    return false;
                }
            }
        }

        static async Task<string> CaptureLiveEditorSourceAsync(string target, string prefix)
            => await CaptureLiveEditorSourceAsync(
                await EditorCaptureSource.CreateAsync(target),
                prefix
            );

        static async Task<string> CaptureLiveEditorSourceAsync(
            EditorCaptureSource source,
            string prefix)
        {
            using (source)
            {
                var staging = GpuCapture.CreateStagingTexture(source.Width, source.Height);
                try
                {
                    if (!source.TryCapture(staging, out var diagnostic))
                        throw new InvalidOperationException(diagnostic);

                    var outputPath = AllocateOutputPath(
                        ConduitAssetPathUtility.GetProjectRootPath(),
                        prefix
                    );
                    await GpuCapture.SavePreparedJpegAsync(staging, outputPath.absolute_path);
                    return $"{outputPath.prefix} image captured: {outputPath.relative_path}";
                }
                finally
                {
                    staging.Release();
                    Object.DestroyImmediate(staging);
                }
            }
        }

        static bool TryCalculateSceneBounds(Scene scene, out Bounds bounds)
        {
            bounds = default;
            var hasBounds = false;
            using var pooledRoots = ConduitUtility.GetPooledList<GameObject>(out var roots);
            using var pooledRenderers = ConduitUtility.GetPooledList<Renderer>(out var renderers);
            scene.GetRootGameObjects(roots);

            foreach (var root in roots)
            {
                if (root == null)
                    continue;

                renderers.Clear();
                root.GetComponentsInChildren(true, renderers);
                foreach (var renderer in renderers)
                {
                    if (renderer == null)
                        continue;

                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                        continue;

                    if (!TryGetMeaningfulBounds(renderer.bounds, out var rendererBounds))
                        continue;

                    if (hasBounds)
                    {
                        bounds.Encapsulate(rendererBounds);
                    }
                    else
                    {
                        bounds = rendererBounds;
                        hasBounds = true;
                    }
                }
            }

            return hasBounds;
        }

        static bool TryCalculateRendererBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            bool hasBounds = false;
            using var handle = ConduitUtility.GetPooledList<Renderer>(out var renderers);
            root.GetComponentsInChildren(true, renderers);
            foreach (var renderer in renderers)
            {
                if (renderer == null)
                    continue;

                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;

                if (!TryGetMeaningfulBounds(renderer.bounds, out var rendererBounds))
                    continue;

                if (hasBounds)
                {
                    bounds.Encapsulate(rendererBounds);
                }
                else
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
            }

            return hasBounds;
        }

        static bool TryGetMeaningfulBounds(Bounds candidate, out Bounds bounds)
        {
            bounds = candidate;
            var size = candidate.size;

            if (!float.IsNormal(size.x))
                return false;

            if (!float.IsNormal(size.y))
                return false;

            if (!float.IsNormal(size.z))
                return false;

            return size.sqrMagnitude > BoundsSizeEpsilon * BoundsSizeEpsilon;
        }

        static Task<string> SaveRenderTextureAsync(
            RenderTexture renderTexture,
            string prefix,
            bool flipVertically = false)
            => SaveTextureAsync(renderTexture, prefix, flipVertically);

        static async Task<string> SaveTextureAsync(Texture texture, string prefix, bool flipVertically = false)
        {
#if MODULE_IMAGECONVERSION
            var outputPath = AllocateOutputPath(ConduitAssetPathUtility.GetProjectRootPath(), prefix);
            await GpuCapture.SaveJpegAsync(
                texture,
                outputPath.absolute_path,
                flipVertically
            );
            return $"{outputPath.prefix} image captured: {outputPath.relative_path}";
#else
            await Task.Yield();
            throw new InvalidOperationException(ModuleUnavailableDiagnostic);
#endif
        }

        internal static ScreenshotOutputPath AllocateOutputPath(string projectPath, string prefix)
        {
            var sanitizedPrefix = SanitizePrefix(prefix);
            var outputDirectoryPath = Path.Combine(projectPath, "Temp", OutputDirectoryName);
            Directory.CreateDirectory(outputDirectoryPath);

            for (var index = 1; index < int.MaxValue; index++)
            {
                var fileName = $"{sanitizedPrefix}_{index}.jpg";
                var absolutePath = Path.Combine(outputDirectoryPath, fileName);
                if (File.Exists(absolutePath))
                    continue;

                return new()
                {
                    prefix = sanitizedPrefix,
                    relative_path = $"Temp/{OutputDirectoryName}/{fileName}",
                    absolute_path = absolutePath,
                };
            }

            throw new InvalidOperationException($"Could not allocate a screenshot output path for '{sanitizedPrefix}'.");
        }

        static string SanitizePrefix(string prefix)
        {
            var trimmed = prefix?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
                return "capture";

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            var previousWasUnderscore = false;
            foreach (var character in trimmed)
            {
                if (builder.Length >= 32)
                    break;

                var mappedCharacter = char.IsLetterOrDigit(character) ? character : '_';
                if (mappedCharacter == '_')
                {
                    if (previousWasUnderscore)
                        continue;

                    previousWasUnderscore = true;
                }
                else
                {
                    previousWasUnderscore = false;
                }

                builder.Append(mappedCharacter);
            }

            var sanitized = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "capture" : sanitized;
        }

        static (int Width, int Height) GetDefaultCaptureSize(float aspect)
        {
            if (aspect <= 0f)
                return (DefaultRenderWidth, DefaultRenderHeight);

            var width = DefaultRenderWidth;
            var height = Mathf.Max(1, Mathf.RoundToInt(width / aspect));
            return height <= DefaultRenderHeight * 2
                ? (width, height)
                : (Mathf.Max(1, Mathf.RoundToInt(DefaultRenderHeight * aspect)), DefaultRenderHeight);
        }

        static void EnsureCanRenderScreenshot(string prefix)
        {
            if (Application.isBatchMode)
                throw new InvalidOperationException($"'{prefix}' screenshots require an interactive Unity editor window.");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException($"'{prefix}' screenshots require a graphics device. Unity is running without one.");
        }

        internal struct ScreenshotOutputPath
        {
            public string prefix;
            public string relative_path;
            public string absolute_path;
        }
    }
}
