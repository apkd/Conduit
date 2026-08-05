#nullable enable

using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Provides allocation-free GPU capture of a live Unity editor view.</summary>
    abstract class EditorCaptureSource : IDisposable
    {
        const string HdrpAssetTypeName = "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset";

        static readonly Type? gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
        static readonly Type? playModeViewType = Type.GetType("UnityEditor.PlayModeView,UnityEditor");
        static readonly MethodInfo? getMainPlayModeViewMethod = playModeViewType?.GetMethod(
            "GetMainPlayModeView",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
        );
        static readonly FieldInfo? gameViewRenderTextureField = gameViewType?.GetField(
            "m_RenderTexture",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly FieldInfo? playModeViewTargetTextureField = playModeViewType?.GetField(
            "m_TargetTexture",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        static readonly FieldInfo? sceneViewTargetTextureField = typeof(SceneView).GetField(
            "m_SceneTargetTexture",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

        protected EditorCaptureSource(string target, int width, int height)
        {
            Target = target;
            Width = width;
            Height = height;
        }

        public string Target { get; }
        public int Width { get; }
        public int Height { get; }
        public abstract bool IsValid { get; }

        /// <summary>Copies the latest view into an already-created destination render texture.</summary>
        public abstract bool TryCapture(RenderTexture destination, out string diagnostic);

        public virtual void Dispose() { }

        public static async Task<EditorCaptureSource> CreateAsync(string? target)
        {
            EnsureInteractive();
            var normalized = target?.Trim() ?? string.Empty;
            if (string.Equals(normalized, "game_view", StringComparison.OrdinalIgnoreCase))
                return await CreateGameViewAsync();

            if (string.Equals(normalized, "scene_view", StringComparison.OrdinalIgnoreCase))
                return await CreateSceneViewAsync();

            if (normalized.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
                return await CreateWindowAsync(normalized);

            throw new InvalidOperationException(
                $"Unsupported recording target '{normalized}'. Use game_view, scene_view, or window:<name>."
            );
        }

        static async Task<EditorCaptureSource> CreateGameViewAsync()
        {
            if (gameViewType == null || playModeViewType == null)
                throw new InvalidOperationException("'game_view' capture is not supported in this Unity version.");

            var window = FindOpenWindow(gameViewType)
                         ?? getMainPlayModeViewMethod?.Invoke(null, null) as EditorWindow
                         ?? EditorWindow.GetWindow(gameViewType);
            if (window == null)
                throw new InvalidOperationException("Could not find or create the Game View window.");

            var texture = await GetRenderedTextureAsync(window, "game_view", () => GetGameViewTexture(window));
            return new RenderedTextureSource(
                "game_view",
                window,
                texture.width,
                texture.height,
                () => GetGameViewTexture(window),
                ShouldFlipGameViewTexture(),
                repaintEachCapture: false
            );
        }

        static async Task<EditorCaptureSource> CreateSceneViewAsync()
        {
            var window = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            if (window == null)
                throw new InvalidOperationException("Could not find or create the Scene View window.");

            var texture = await GetRenderedTextureAsync(window, "scene_view", () => GetSceneViewTexture(window));
            return new RenderedTextureSource(
                "scene_view",
                window,
                texture.width,
                texture.height,
                () => GetSceneViewTexture(window),
                flipVertically: false,
                repaintEachCapture: true
            );
        }

        static async Task<EditorCaptureSource> CreateWindowAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target["window:".Length..]))
                throw new InvalidOperationException("Editor window recording target was empty.");

            var matches = ConduitSearchUtility.Resolve(target);
            if (matches.Count == 0)
                throw new InvalidOperationException($"No matches for '{target}'.");

            if (matches.Count > 1)
                throw new InvalidOperationException(ConduitSearchUtility.FormatMatches(matches, includeHint: true));

            if (matches[0].Target is not EditorWindow window)
                throw new InvalidOperationException($"Target '{matches[0].Name}' is not an editor window.");

            window.ShowTab();
            window.Focus();
            window.Repaint();
            await WaitForNextEditorUpdateAsync();
            await WaitForNextEditorUpdateAsync();

            var size = GetWindowPixelSize(window.position);
            return new EditorWindowSource(
                ConduitSearchUtility.GetEditorWindowDisplayName(window),
                window,
                Mathf.RoundToInt(size.x),
                Mathf.RoundToInt(size.y)
            );
        }

        static async Task<RenderTexture> GetRenderedTextureAsync(
            EditorWindow window,
            string target,
            Func<RenderTexture?> getTexture)
        {
            // hidden dock tabs can retain a valid but stale render texture
            window.ShowTab();
            window.Focus();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                window.Repaint();
                await WaitForNextEditorUpdateAsync();
                var texture = getTexture();
                if (IsUsable(texture))
                    return texture!;
            }

            throw new InvalidOperationException($"Unity did not expose a rendered texture for '{target}'.");
        }

        internal static Task WaitForNextEditorUpdateAsync()
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EditorApplication.update += Complete;
            return completion.Task;

            void Complete()
            {
                EditorApplication.update -= Complete;
                completion.TrySetResult(true);
            }
        }

        internal static bool ShouldFlipGameViewTexture()
        {
            if (SystemInfo.graphicsUVStartsAtTop)
                return true;

            // hdrp keeps its editor presentation target inverted after the final game-view blit
            for (var type = GraphicsSettings.currentRenderPipeline?.GetType(); type != null; type = type.BaseType)
                if (string.Equals(type.FullName, HdrpAssetTypeName, StringComparison.Ordinal))
                    return true;

            return false;
        }

        internal static Vector2 GetWindowPixelSize(Rect position)
        {
            var pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            return new(
                Mathf.Max(1, Mathf.RoundToInt(position.width * pixelsPerPoint)),
                Mathf.Max(1, Mathf.RoundToInt(position.height * pixelsPerPoint))
            );
        }

        static RenderTexture? GetGameViewTexture(EditorWindow window)
        {
            var texture = gameViewRenderTextureField?.GetValue(window) as RenderTexture;
            return IsUsable(texture)
                ? texture
                : playModeViewTargetTextureField?.GetValue(window) as RenderTexture;
        }

        static RenderTexture? GetSceneViewTexture(SceneView window)
            => sceneViewTargetTextureField?.GetValue(window) as RenderTexture;

        static bool IsUsable(RenderTexture? texture)
            => texture != null && texture.IsCreated() && texture.width > 0 && texture.height > 0;

        static EditorWindow? FindOpenWindow(Type windowType)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window)
                    return window;

            return null;
        }

        static void EnsureInteractive()
        {
            if (Application.isBatchMode)
                throw new InvalidOperationException("Editor view capture requires an interactive Unity editor window.");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException("Editor view capture requires a graphics device.");
        }

        sealed class RenderedTextureSource : EditorCaptureSource
        {
            readonly EditorWindow window;
            readonly Func<RenderTexture?> getTexture;
            readonly bool flipVertically;
            readonly bool repaintEachCapture;

            public RenderedTextureSource(
                string target,
                EditorWindow window,
                int width,
                int height,
                Func<RenderTexture?> getTexture,
                bool flipVertically,
                bool repaintEachCapture)
                : base(target, width, height)
            {
                this.window = window;
                this.getTexture = getTexture;
                this.flipVertically = flipVertically;
                this.repaintEachCapture = repaintEachCapture;
            }

            public override bool TryCapture(RenderTexture destination, out string diagnostic)
            {
                if (window == null)
                {
                    diagnostic = $"The '{Target}' window was closed during recording.";
                    return false;
                }

                if (repaintEachCapture)
                    window.Repaint();

                var source = getTexture();
                if (!IsUsable(source))
                {
                    diagnostic = $"Unity temporarily has no rendered texture for '{Target}'.";
                    return false;
                }

                Graphics.Blit(
                    source,
                    destination,
                    flipVertically ? new(1f, -1f) : Vector2.one,
                    flipVertically ? new(0f, 1f) : Vector2.zero
                );
                diagnostic = string.Empty;
                return true;
            }

            public override bool IsValid => window != null;
        }

        sealed class EditorWindowSource : EditorCaptureSource
        {
            readonly EditorWindow window;
            readonly RenderTexture source;

            public EditorWindowSource(string target, EditorWindow window, int width, int height)
                : base(target, width, height)
            {
                this.window = window;
                source = new(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                source.Create();
            }

            public override bool TryCapture(RenderTexture destination, out string diagnostic)
            {
                if (window == null)
                {
                    diagnostic = $"The editor window '{Target}' was closed during recording.";
                    return false;
                }

                window.ShowTab();
                window.Repaint();
                if (!UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow(window, source))
                {
                    diagnostic = $"Unity failed to capture editor window '{Target}'.";
                    return false;
                }

                Graphics.Blit(source, destination);
                diagnostic = string.Empty;
                return true;
            }

            public override bool IsValid => window != null;

            public override void Dispose()
            {
                source.Release();
                Object.DestroyImmediate(source);
            }
        }
    }
}
