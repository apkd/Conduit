#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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

        static readonly Type? gameViewType
            = Type.GetType("UnityEditor.GameView,UnityEditor");

        static readonly Type? playModeViewType
            = Type.GetType("UnityEditor.PlayModeView,UnityEditor");

        static readonly MethodInfo? getMainPlayModeViewMethod
            = playModeViewType?.GetMethod("GetMainPlayModeView", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        static readonly FieldInfo? gameViewRenderTextureField
            = gameViewType?.GetField("m_RenderTexture", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo? playModeViewTargetTextureField
            = playModeViewType?.GetField("m_TargetTexture", BindingFlags.Instance | BindingFlags.NonPublic);

        static readonly FieldInfo? sceneViewTargetTextureField
            = typeof(SceneView).GetField("m_SceneTargetTexture", BindingFlags.Instance | BindingFlags.NonPublic);

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
                return CaptureCamera(camera, match.Name);

            if (TryGetPreviewSource(target, out var previewSource))
                return CaptureGameObjectPreview(previewSource, match.Name);

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
            if (Application.isBatchMode)
                throw new InvalidOperationException($"'{target}' screenshots require an interactive Unity editor window.");

            EnsureHasGraphicsDevice(target);
            if (string.IsNullOrWhiteSpace(target["window:".Length..].Trim()))
                throw new InvalidOperationException("Editor window screenshot target was empty.");

            return ConduitSearchUtility.Resolve(target) switch
            {
                { Count: 0 }           => $"No matches for '{target}'.",
                { Count: 1 } matches   => matches[0].Target is EditorWindow window
                    ? await CaptureEditorWindowAsync(window, ConduitSearchUtility.GetEditorWindowDisplayName(window))
                    : throw new InvalidOperationException($"Target '{matches[0].Name}' is not an editor window."),
                { Count: > 1 } matches => ConduitSearchUtility.FormatMatches(matches, includeHint: true),
            };
        }

        static async Task<string> CaptureGameViewAsync()
        {
            if (Application.isBatchMode)
                throw new InvalidOperationException("'game_view' screenshots require an interactive Unity editor window.");

            EnsureHasGraphicsDevice("game_view");
            if (gameViewType == null || playModeViewType == null)
                throw new InvalidOperationException("'game_view' screenshots are not supported in this Unity version.");

            var window = FindOpenWindow(gameViewType)
                         ?? getMainPlayModeViewMethod?.Invoke(null, null) as EditorWindow
                         ?? EditorWindow.GetWindow(gameViewType);

            if (window == null)
                throw new InvalidOperationException("Could not find or create the Game View window.");

            var renderTexture = await GetWindowRenderTextureAsync(
                window,
                "game_view",
                TryGetGameViewRenderTexture
            );

            return SaveTexture(renderTexture, "game_view");
        }

        static async Task<string> CaptureSceneViewAsync()
        {
            EnsureHasGraphicsDevice("scene_view");
            var window = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            if (window == null)
                throw new InvalidOperationException("Could not find or create the Scene View window.");

            return await Task.FromResult(CaptureSceneView(window));
        }

        static async Task<string> CaptureSceneAssetAsync(string sceneAssetPath)
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Scene asset screenshots are only supported in edit mode.");

            EnsureHasGraphicsDevice(Path.GetFileNameWithoutExtension(sceneAssetPath));
            var previewScene = EditorSceneManager.OpenPreviewScene(sceneAssetPath);
            try
            {
                await WaitForNextEditorUpdateAsync();
                if (!TryCalculateSceneBounds(previewScene, out var bounds))
                    throw new InvalidOperationException($"Scene '{sceneAssetPath}' has no visible renderer bounds to capture.");

                return CaptureSceneBounds(
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

        static string CaptureCamera(Camera sourceCamera, string prefix)
        {
            EnsureHasGraphicsDevice(prefix);
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
                return SaveRenderTexture(renderTexture, prefix);
            }
            finally
            {
                RenderTexture.active = previousActive;
                sourceCamera.targetTexture = previousTargetTexture;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static string CaptureGameObjectPreview(GameObject previewSource, string prefix)
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

                return CaptureSceneBounds(previewScene, bounds, prefix, topDown: false);
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

            return SaveTexture(previewTexture, prefix);
        }

        static bool IsLoadingAssetPreview(Object target)
        {
#if UNITY_6000_4_OR_NEWER
            return AssetPreview.IsLoadingAssetPreview(target.GetEntityId());
#else
            return AssetPreview.IsLoadingAssetPreview(target.GetInstanceID());
#endif
        }

        static string CaptureSceneBounds(Scene scene, Bounds bounds, string prefix, bool topDown)
        {
            EnsureHasGraphicsDevice(prefix);
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

                if (topDown)
                    ConfigureTopDownCamera(camera, bounds, (float)width / height);
                else
                    ConfigurePreviewCamera(camera, bounds);

                keyLightObject = CreateDirectionalLight(scene, "__ConduitScreenshotKeyLight", new(50f, 330f, 0f), 1.2f);
                fillLightObject = CreateDirectionalLight(scene, "__ConduitScreenshotFillLight", new(340f, 35f, 0f), 0.55f);

                camera.Render();
                RenderTexture.active = renderTexture;
                return SaveRenderTexture(renderTexture, prefix);
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

        static async Task<RenderTexture> GetWindowRenderTextureAsync<TWindow>(TWindow window, string targetName, Func<TWindow, RenderTexture?> getRenderTexture)
            where TWindow : EditorWindow
        {
            var renderTexture = getRenderTexture(window);
            if (IsUsableRenderTexture(renderTexture))
                return renderTexture!;

            window.Focus();
            window.Repaint();
            await WaitForNextEditorUpdateAsync();
            renderTexture = getRenderTexture(window);
            if (IsUsableRenderTexture(renderTexture))
                return renderTexture!;

            await WaitForNextEditorUpdateAsync();
            renderTexture = getRenderTexture(window);
            if (IsUsableRenderTexture(renderTexture))
                return renderTexture!;

            throw new InvalidOperationException($"Unity did not expose a rendered texture for '{targetName}'.");
        }

        static RenderTexture? TryGetGameViewRenderTexture(EditorWindow window)
        {
            var renderTexture = gameViewRenderTextureField?.GetValue(window) as RenderTexture;
            if (IsUsableRenderTexture(renderTexture))
                return renderTexture;

            return playModeViewTargetTextureField?.GetValue(window) as RenderTexture;
        }

        static RenderTexture? TryGetSceneViewRenderTexture(SceneView window)
            => sceneViewTargetTextureField?.GetValue(window) as RenderTexture;

        static bool IsUsableRenderTexture(RenderTexture? renderTexture)
            => renderTexture != null
               && renderTexture.IsCreated()
               && renderTexture.width > 0
               && renderTexture.height > 0;

        static async Task<string> CaptureEditorWindowAsync(EditorWindow window, string prefix)
        {
            window.Focus();
            window.Repaint();
            await WaitForNextEditorUpdateAsync();
            if (EditorWindow.focusedWindow != window)
                throw new InvalidOperationException($"Editor window '{ConduitSearchUtility.GetEditorWindowDisplayName(window)}' could not be focused for capture.");

            var renderSize = GetWindowRenderSize(window.position);
            var renderTexture = new RenderTexture(
                Mathf.Max(1, Mathf.RoundToInt(renderSize.x)),
                Mathf.Max(1, Mathf.RoundToInt(renderSize.y)),
                24,
                RenderTextureFormat.ARGB32
            );
            var previousActive = RenderTexture.active;

            try
            {
                renderTexture.Create();
                if (!UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow(window, renderTexture))
                    throw new InvalidOperationException($"Unity failed to capture editor window '{ConduitSearchUtility.GetEditorWindowDisplayName(window)}'.");

                return SaveTexture(renderTexture, prefix);
            }
            finally
            {
                if (RenderTexture.active == renderTexture)
                    RenderTexture.active = previousActive;

                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        static Task WaitForNextEditorUpdateAsync()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.delayCall += Complete;
            return completion.Task;

            void Complete()
                => completion.TrySetResult(true);
        }

        static string CaptureSceneView(SceneView window)
        {
            var sceneViewTexture = TryGetSceneViewRenderTexture(window);
            var renderSize = IsUsableRenderTexture(sceneViewTexture)
                ? new(sceneViewTexture!.width, sceneViewTexture.height)
                : GetWindowRenderSize(window.position);

            var (width, height) = (Mathf.RoundToInt(renderSize.x), Mathf.RoundToInt(renderSize.y));
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var camera = window.camera;
            if (camera == null)
                throw new InvalidOperationException("Scene View does not have an active camera.");

            var previousTargetTexture = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                return SaveRenderTexture(renderTexture, "scene_view");
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTargetTexture;
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
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

        static Vector2 GetWindowRenderSize(Rect position)
        {
            var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            var width = Mathf.Max(1, Mathf.RoundToInt(position.width * pixelsPerPoint));
            var height = Mathf.Max(1, Mathf.RoundToInt(position.height * pixelsPerPoint));
            return new(width, height);
        }

        static string SaveRenderTexture(RenderTexture renderTexture, string prefix, bool flipVertically = false)
        {
            var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
            try
            {
                texture.ReadPixels(new(0f, 0f, renderTexture.width, renderTexture.height), 0, 0);
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                if (flipVertically)
                    FlipTextureVertically(texture);

                return SaveTexture(texture, prefix);
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        static void FlipTextureVertically(Texture2D texture)
        {
            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var rowLength = width;
            var halfHeight = height / 2;

            for (var y = 0; y < halfHeight; y++)
            {
                var topRow = y * rowLength;
                var bottomRow = (height - 1 - y) * rowLength;
                for (var x = 0; x < rowLength; x++)
                    (pixels[topRow + x], pixels[bottomRow + x]) = (pixels[bottomRow + x], pixels[topRow + x]);
            }

            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        }

        static string SaveTexture(Texture texture, string prefix)
        {
            var readableTexture = ToReadableTexture(texture);
            try
            {
                var outputPath = AllocateOutputPath(ConduitAssetPathUtility.GetProjectRootPath(), prefix);
                File.WriteAllBytes(outputPath.absolute_path, readableTexture.EncodeToJPG(95));
                return $"{outputPath.prefix} image captured: {outputPath.relative_path}";
            }
            finally
            {
                Object.DestroyImmediate(readableTexture);
            }
        }

        static Texture2D ToReadableTexture(Texture texture)
        {
            if (texture is Texture2D readableTexture && readableTexture.isReadable)
            {
                var copy = new Texture2D(readableTexture.width, readableTexture.height, TextureFormat.RGB24, false);
                copy.SetPixels(readableTexture.GetPixels());
                copy.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return copy;
            }

            var width = Mathf.Max(1, texture.width);
            var height = Mathf.Max(1, texture.height);
            var renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(texture, renderTexture);
                RenderTexture.active = renderTexture;

                var readable = new Texture2D(width, height, TextureFormat.RGB24, false);
                readable.ReadPixels(new(0f, 0f, width, height), 0, 0);
                readable.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                return readable;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
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

        static EditorWindow? FindOpenWindow(Type windowType)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window)
                    return window;

            return null;
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

        static void EnsureHasGraphicsDevice(string prefix)
        {
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
