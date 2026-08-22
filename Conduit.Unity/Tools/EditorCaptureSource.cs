#nullable enable

using System;
using System.Collections.Generic;
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
        const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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
        static readonly FieldInfo? editorWindowParentField = typeof(EditorWindow).GetField(
            "m_Parent",
            InstanceMembers
        );
        static readonly PropertyInfo? hostViewActualViewProperty = typeof(EditorWindow).Assembly
            .GetType("UnityEditor.HostView")
            ?.GetProperty("actualView", InstanceMembers);
        static readonly MethodInfo? grabPixelsMethod = typeof(EditorWindow).Assembly
            .GetType("UnityEditor.GUIView")
            ?.GetMethod("GrabPixels", InstanceMembers);

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

            var initialState = EditorWindowState.Capture();
            var window = FindOpenWindow(gameViewType);
            if (window == null)
                window = getMainPlayModeViewMethod?.Invoke(null, null) as EditorWindow;

            if (window == null)
                window = ConduitEditorWindowDocking.CreateDockedTab(gameViewType);

            if (window == null)
                throw new InvalidOperationException("Could not find or create the Game View window.");

            return await CreateRenderedTextureSourceAsync(
                "game_view",
                window,
                initialState,
                () => GetGameViewTexture(window),
                ShouldFlipGameViewTexture()
            );
        }

        static async Task<EditorCaptureSource> CreateSceneViewAsync()
        {
            var initialState = EditorWindowState.Capture();
            var window = SceneView.lastActiveSceneView;
            if (window == null)
                window = FindOpenWindow(typeof(SceneView)) as SceneView;

            if (window == null)
                window = (SceneView)ConduitEditorWindowDocking.CreateDockedTab(typeof(SceneView));

            if (window == null)
                throw new InvalidOperationException("Could not find or create the Scene View window.");

            return await CreateWindowAsync(
                window,
                "scene_view",
                initialState
            );
        }

        static async Task<EditorCaptureSource> CreateRenderedTextureSourceAsync(
            string target,
            EditorWindow window,
            EditorWindowState initialState,
            Func<RenderTexture?> getTexture,
            bool flipVertically)
        {
            var previousTab = initialState.GetTabToRestore(window);
            var ownsWindow = !initialState.Contains(window);
            try
            {
                ConduitEditorWindowDocking.EnsureCanShow(window, target);
                var texture = await GetRenderedTextureAsync(window, target, getTexture);
                return new RenderedTextureSource(
                    target,
                    window,
                    texture.width,
                    texture.height,
                    getTexture,
                    flipVertically,
                    previousTab,
                    ownsWindow
                );
            }
            catch
            {
                RestoreWindowState(window, previousTab, ownsWindow);
                throw;
            }
        }

        static async Task<EditorCaptureSource> CreateWindowAsync(string target)
        {
            if (string.IsNullOrWhiteSpace(target["window:".Length..]))
                throw new InvalidOperationException("Editor window recording target was empty.");

            var initialState = EditorWindowState.Capture();
            var matches = ConduitSearchUtility.Resolve(target);
            if (matches.Count == 0)
                throw new InvalidOperationException($"No matches for '{target}'.");

            if (matches.Count > 1)
                throw new InvalidOperationException(ConduitSearchUtility.FormatMatches(matches, includeHint: true));

            if (matches[0].Target is not EditorWindow window)
                throw new InvalidOperationException($"Target '{matches[0].Name}' is not an editor window.");

            return await CreateWindowAsync(
                window,
                ConduitSearchUtility.GetEditorWindowDisplayName(window),
                initialState
            );
        }

        internal static async Task<EditorCaptureSource> CreateWindowAsync(
            EditorWindow window,
            string displayName,
            EditorWindowState initialState)
        {
            var previousTab = initialState.GetTabToRestore(window);
            var ownsWindow = !initialState.Contains(window);
            try
            {
                ConduitEditorWindowDocking.EnsureCanShow(window, displayName);
                window.ShowTab();
                window.Repaint();
                await WaitForNextEditorUpdateAsync();
                await WaitForNextEditorUpdateAsync();

                var size = GetWindowPixelSize(window.position);
                return new EditorWindowSource(
                    displayName,
                    window,
                    Mathf.RoundToInt(size.x),
                    Mathf.RoundToInt(size.y),
                    CreateGrabPixelsDelegate(window),
                    previousTab,
                    ownsWindow
                );
            }
            catch
            {
                RestoreWindowState(window, previousTab, ownsWindow);
                throw;
            }
        }

        static async Task<RenderTexture> GetRenderedTextureAsync(
            EditorWindow window,
            string target,
            Func<RenderTexture?> getTexture)
        {
            // hidden dock tabs can retain a valid but stale render texture
            window.ShowTab();
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

        static bool IsUsable(RenderTexture? texture)
            => texture != null && texture.IsCreated() && texture.width > 0 && texture.height > 0;

        static EditorWindow? FindOpenWindow(Type windowType)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window)
                    return window;

            return null;
        }

        static Action<RenderTexture, Rect> CreateGrabPixelsDelegate(EditorWindow window)
        {
            var parent = GetParent(window)
                         ?? throw new InvalidOperationException(
                             $"Editor window '{window.titleContent.text}' has no host view."
                         );
            var method = grabPixelsMethod
                         ?? throw new MissingMethodException("UnityEditor.GUIView.GrabPixels");
            return method.CreateDelegate(typeof(Action<RenderTexture, Rect>), parent)
                       as Action<RenderTexture, Rect>
                   ?? throw new InvalidOperationException(
                       "Unity did not expose focus-free editor window capture."
                   );
        }

        static object? GetParent(EditorWindow window)
            => editorWindowParentField?.GetValue(window);

        static EditorWindow? GetSelectedTab(object parent)
            => hostViewActualViewProperty?.GetValue(parent) as EditorWindow;

        static bool IsSelectedTab(EditorWindow window)
            => GetParent(window) is { } parent
               && ReferenceEquals(GetSelectedTab(parent), window);

        static void RestoreWindowState(
            EditorWindow window,
            EditorWindow? previousTab,
            bool ownsWindow)
        {
            if (window == null)
                return;

            if (ownsWindow)
            {
                window.Close();
                if (previousTab != null)
                    previousTab.ShowTab();

                return;
            }

            var parent = GetParent(window);
            if (previousTab != null
                && parent != null
                && ReferenceEquals(GetParent(previousTab), parent)
                && ReferenceEquals(GetSelectedTab(parent), window))
                previousTab.ShowTab();
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
            readonly EditorWindow? previousTab;
            readonly bool ownsWindow;

            public RenderedTextureSource(
                string target,
                EditorWindow window,
                int width,
                int height,
                Func<RenderTexture?> getTexture,
                bool flipVertically,
                EditorWindow? previousTab,
                bool ownsWindow)
                : base(target, width, height)
            {
                this.window = window;
                this.getTexture = getTexture;
                this.flipVertically = flipVertically;
                this.previousTab = previousTab;
                this.ownsWindow = ownsWindow;
            }

            public override bool TryCapture(RenderTexture destination, out string diagnostic)
            {
                if (window == null)
                {
                    diagnostic = $"The '{Target}' window was closed during recording.";
                    return false;
                }

                if (!IsSelectedTab(window))
                {
                    diagnostic = $"The '{Target}' window is no longer the selected tab.";
                    return false;
                }

                var source = getTexture();
                if (!IsUsable(source))
                {
                    diagnostic = $"Unity temporarily has no rendered texture for '{Target}'.";
                    return false;
                }

                var previousActive = RenderTexture.active;
                try
                {
                    Graphics.Blit(
                        source,
                        destination,
                        flipVertically ? new(1f, -1f) : Vector2.one,
                        flipVertically ? new(0f, 1f) : Vector2.zero
                    );
                    diagnostic = string.Empty;
                    return true;
                }
                finally
                {
                    RenderTexture.active = previousActive;
                }
            }

            public override bool IsValid => window != null;

            public override void Dispose()
                => RestoreWindowState(window, previousTab, ownsWindow);
        }

        sealed class EditorWindowSource : EditorCaptureSource
        {
            static readonly Material captureMaterial =
                (Material)EditorGUIUtility.LoadRequired("SceneView/BlitSceneViewCapture.mat");

            readonly EditorWindow window;
            readonly RenderTexture source;
            readonly Action<RenderTexture, Rect> grabPixels;
            readonly EditorWindow? previousTab;
            readonly bool ownsWindow;

            public EditorWindowSource(
                string target,
                EditorWindow window,
                int width,
                int height,
                Action<RenderTexture, Rect> grabPixels,
                EditorWindow? previousTab,
                bool ownsWindow)
                : base(target, width, height)
            {
                this.window = window;
                this.grabPixels = grabPixels;
                this.previousTab = previousTab;
                this.ownsWindow = ownsWindow;
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

                if (!IsSelectedTab(window))
                {
                    diagnostic = $"The editor window '{Target}' is no longer the selected tab.";
                    return false;
                }

                try
                {
                    ConduitEditorWindowDocking.EnsureCanShow(window, Target);
                }
                catch (InvalidOperationException exception)
                {
                    diagnostic = exception.Message;
                    return false;
                }

                window.Repaint();
                var previousActive = RenderTexture.active;
                var temporary = RenderTexture.GetTemporary(source.descriptor);
                try
                {
                    // unity's wrapper makes this same backing-buffer capture conditional on window focus
                    grabPixels(temporary, new(0f, 0f, window.position.width, window.position.height));
                    Graphics.Blit(temporary, source, captureMaterial);
                    Graphics.Blit(source, destination);
                    diagnostic = string.Empty;
                    return true;
                }
                catch (Exception exception)
                {
                    diagnostic = $"Unity failed to capture editor window '{Target}': {exception.Message}";
                    return false;
                }
                finally
                {
                    RenderTexture.active = previousActive;
                    RenderTexture.ReleaseTemporary(temporary);
                }
            }

            public override bool IsValid => window != null;

            public override void Dispose()
            {
                try
                {
                    source.Release();
                    Object.DestroyImmediate(source);
                }
                finally
                {
                    RestoreWindowState(window, previousTab, ownsWindow);
                }
            }
        }

        internal sealed class EditorWindowState
        {
            readonly EditorWindow[] windows;
            readonly (EditorWindow Window, object Parent)[] selectedTabs;

            EditorWindowState(
                EditorWindow[] windows,
                (EditorWindow Window, object Parent)[] selectedTabs)
            {
                this.windows = windows;
                this.selectedTabs = selectedTabs;
            }

            internal static EditorWindowState Capture()
            {
                var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
                var selectedTabs = new List<(EditorWindow, object)>();
                foreach (var window in windows)
                {
                    var parent = GetParent(window);
                    if (parent != null && ReferenceEquals(GetSelectedTab(parent), window))
                        selectedTabs.Add((window, parent));
                }

                return new(windows, selectedTabs.ToArray());
            }

            internal bool Contains(EditorWindow window)
            {
                foreach (var existing in windows)
                    if (ReferenceEquals(existing, window))
                        return true;

                return false;
            }

            internal EditorWindow? GetTabToRestore(EditorWindow window)
            {
                var parent = GetParent(window);
                foreach (var selectedTab in selectedTabs)
                    if (!ReferenceEquals(selectedTab.Window, window)
                        && ReferenceEquals(selectedTab.Parent, parent))
                        return selectedTab.Window;

                return null;
            }
        }
    }
}
