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
    /// <summary>A live Unity editor view that can be copied without activating its OS window.</summary>
    abstract class EditorCaptureSource : IDisposable
    {
        const string HdrpAssetTypeName = "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset";
        const string GrabPixelsMaterialPath = "SceneView/BlitSceneViewCapture.mat";
        const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Type? containerWindowType = typeof(EditorWindow).Assembly
            .GetType("UnityEditor.ContainerWindow");
        static readonly Type? viewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.View");
        static readonly Type? guiViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
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
        static readonly PropertyInfo? containerWindowsProperty = containerWindowType?.GetProperty(
            "windows",
            StaticMembers
        );
        static readonly PropertyInfo? containerRootViewProperty = containerWindowType?.GetProperty(
            "rootView",
            InstanceMembers
        );
        static readonly PropertyInfo? containerShowModeProperty = containerWindowType?.GetProperty(
            "showMode",
            InstanceMembers
        );
        static readonly PropertyInfo? containerTitleProperty = containerWindowType?.GetProperty(
            "title",
            InstanceMembers
        );
        static readonly PropertyInfo? viewChildrenProperty = viewType?.GetProperty(
            "children",
            InstanceMembers
        );
        static readonly PropertyInfo? viewWindowPositionProperty = viewType?.GetProperty(
            "windowPosition",
            InstanceMembers
        );
        static readonly MethodInfo? guiViewRepaintMethod = guiViewType?.GetMethod(
            "Repaint",
            InstanceMembers
        );
        static readonly MethodInfo? guiViewBackingScaleFactorMethod = guiViewType?.GetMethod(
            "GetBackingScaleFactor",
            InstanceMembers
        );
        static readonly MethodInfo? grabPixelsMethod = guiViewType?.GetMethod(
            "GrabPixels",
            InstanceMembers
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

        /// <summary>Creates a focus-free source for a supported editor view target.</summary>
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

        internal static async Task<EditorCaptureSource[]> CreateEditorSourcesAsync()
        {
            EnsureInteractive();
            if (containerWindowsProperty?.GetValue(null) is not System.Collections.IEnumerable windows)
                throw new MissingMemberException("Unity editor container windows");

            // a ContainerWindow has no combined backing buffer, so each stable layout window is
            // reconstructed from the GUIViews that Unity already rendered for it.
            var containers = new List<(object rootView, string target)>();
            bool hasMainWindow = false;
            foreach (var container in windows)
            {
                if (container == null)
                    continue;

                var showMode = containerShowModeProperty?.GetValue(container)?.ToString();
                // utility and auxiliary modes also host modal and transient surfaces;
                // persistent editor layout windows use MainWindow or NormalWindow
                if (showMode is not ("MainWindow" or "NormalWindow"))
                    continue;

                var rootView = containerRootViewProperty?.GetValue(container);
                if (rootView is not Object unityRootView || unityRootView == null)
                    continue;

                if (showMode == "MainWindow")
                {
                    containers.Insert(0, (rootView, "editor"));
                    hasMainWindow = true;
                    continue;
                }

                var title = containerTitleProperty?.GetValue(container) as string;
                containers.Add(
                    (rootView, string.IsNullOrWhiteSpace(title) ? "editor window" : $"editor {title}")
                );
            }

            if (!hasMainWindow)
                throw new InvalidOperationException("Could not find Unity's main editor window.");

            var views = new List<object>();
            var repaint = guiViewRepaintMethod
                          ?? throw new MissingMethodException("UnityEditor.GUIView.Repaint");
            foreach (var container in containers)
            {
                views.Clear();
                CollectGuiViews(container.rootView, views);
                foreach (var view in views)
                    repaint.Invoke(view, null);
            }

            // repaint is deferred; two updates let IMGUI and UI Toolkit publish the backing buffers
            await WaitForNextEditorUpdateAsync();
            await WaitForNextEditorUpdateAsync();

            var sources = new List<EditorCaptureSource>(containers.Count);
            foreach (var container in containers)
                sources.Add(CreateSource(container.rootView, container.target));

            return sources.ToArray();

            static EditorCaptureSource CreateSource(object rootView, string target)
            {
                if (rootView is not Object unityRootView || unityRootView == null)
                    throw new InvalidOperationException($"Unity editor window '{target}' closed during capture.");

                var rootPosition = GetViewPosition(rootView);
                var views = new List<object>();
                CollectGuiViews(rootView, views);
                if (views.Count == 0)
                    throw new InvalidOperationException($"Unity editor window '{target}' has no visible GUI views.");

                // GUIView geometry and GrabPixels regions use points; destinations use backing pixels
                // query the view because detached windows may be on a different-DPI monitor
                float scale = GetBackingScaleFactor(views[0]);
                int width = Mathf.Max(1, Mathf.RoundToInt(rootPosition.width * scale));
                int height = Mathf.Max(1, Mathf.RoundToInt(rootPosition.height * scale));
                var captures = new List<GuiViewCapture>(views.Count);
                foreach (var view in views)
                {
                    var position = GetViewPosition(view);
                    int xMin = Mathf.Clamp(
                        Mathf.RoundToInt((position.xMin - rootPosition.xMin) * scale),
                        0,
                        width
                    );
                    int xMax = Mathf.Clamp(
                        Mathf.RoundToInt((position.xMax - rootPosition.xMin) * scale),
                        0,
                        width
                    );
                    int yMin = Mathf.Clamp(
                        Mathf.RoundToInt((position.yMin - rootPosition.yMin) * scale),
                        0,
                        height
                    );
                    int yMax = Mathf.Clamp(
                        Mathf.RoundToInt((position.yMax - rootPosition.yMin) * scale),
                        0,
                        height
                    );
                    var pixelRect = new RectInt(
                        xMin,
                        height - yMax,
                        xMax - xMin,
                        yMax - yMin
                    );
                    if (pixelRect is not { width: > 0, height: > 0 }
                        || view is not Object unityView
                        || unityView == null)
                        continue;

                    captures.Add(
                        new(
                            unityView,
                            new(0f, 0f, position.width, position.height),
                            pixelRect,
                            CreateGrabPixelsDelegate(view)
                        )
                    );
                }

                if (captures.Count == 0)
                    throw new InvalidOperationException($"Unity editor window '{target}' has no capturable GUI views.");

                return new EditorCompositeSource(
                    target,
                    unityRootView,
                    width,
                    height,
                    captures.ToArray()
                );
            }

            static void CollectGuiViews(object view, List<object> destination)
            {
                if (guiViewType?.IsInstanceOfType(view) == true)
                {
                    destination.Add(view);
                    return;
                }

                if (viewChildrenProperty?.GetValue(view) is not Array children)
                    return;

                foreach (var child in children)
                    if (child != null)
                        CollectGuiViews(child, destination);
            }
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
            bool ownsWindow = !initialState.Contains(window);
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
            bool ownsWindow = !initialState.Contains(window);
            try
            {
                ConduitEditorWindowDocking.EnsureCanShow(window, displayName);
                window.ShowTab();
                window.Repaint();
                await WaitForNextEditorUpdateAsync();
                await WaitForNextEditorUpdateAsync();

                var parent = GetParent(window)
                             ?? throw new InvalidOperationException(
                                 $"Editor window '{window.titleContent.text}' has no host view."
                             );
                float scale = GetBackingScaleFactor(parent);
                int width = Mathf.Max(1, Mathf.RoundToInt(window.position.width * scale));
                int height = Mathf.Max(1, Mathf.RoundToInt(window.position.height * scale));
                return new EditorWindowSource(
                    displayName,
                    window,
                    width,
                    height,
                    CreateGrabPixelsDelegate(parent),
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
            for (int attempt = 0; attempt < 2; ++attempt)
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

        static RenderTexture? GetGameViewTexture(EditorWindow window)
        {
            var texture = gameViewRenderTextureField?.GetValue(window) as RenderTexture;
            return IsUsable(texture)
                ? texture
                : playModeViewTargetTextureField?.GetValue(window) as RenderTexture;
        }

        static bool IsUsable(RenderTexture? texture)
            => texture != null && texture.IsCreated() && texture.width > 0 && texture.height > 0;

        static float GetBackingScaleFactor(object guiView)
        {
            var value = guiViewBackingScaleFactorMethod?.Invoke(guiView, null);
            if (value is float scale && float.IsFinite(scale) && scale > 0f)
                return scale;

            float fallback = EditorGUIUtility.pixelsPerPoint;
            return float.IsFinite(fallback) && fallback > 0f ? fallback : 1f;
        }

        static EditorWindow? FindOpenWindow(Type windowType)
        {
            foreach (var candidate in Resources.FindObjectsOfTypeAll(windowType))
                if (candidate is EditorWindow window)
                    return window;

            return null;
        }

        static Rect GetViewPosition(object view)
            => viewWindowPositionProperty?.GetValue(view) is Rect position
                ? position
                : throw new MissingMemberException("Unity editor view position");

        static Action<RenderTexture, Rect> CreateGrabPixelsDelegate(object guiView)
        {
            var method = grabPixelsMethod
                         ?? throw new MissingMethodException("UnityEditor.GUIView.GrabPixels");
            return method.CreateDelegate(typeof(Action<RenderTexture, Rect>), guiView)
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

        sealed class EditorCompositeSource : EditorCaptureSource
        {
            static readonly Material captureMaterial =
                (Material)EditorGUIUtility.LoadRequired(GrabPixelsMaterialPath);

            readonly Object rootView;
            readonly GuiViewCapture[] views;

            internal EditorCompositeSource(
                string target,
                Object rootView,
                int width,
                int height,
                GuiViewCapture[] views)
                : base(target, width, height)
            {
                this.rootView = rootView;
                this.views = views;
            }

            public override bool TryCapture(RenderTexture destination, out string diagnostic)
            {
                if (rootView == null)
                {
                    diagnostic = $"Unity editor window '{Target}' was closed during capture.";
                    return false;
                }

                var previousActive = RenderTexture.active;
                try
                {
                    RenderTexture.active = destination;
                    GL.Clear(false, true, new(0.1f, 0.1f, 0.1f, 1f));
                    // every GUIView owns a separate backing buffer; copy each one to its container-relative position
                    foreach (var view in views)
                    {
                        if (view.View == null)
                        {
                            diagnostic = $"Unity editor window '{Target}' changed during capture.";
                            return false;
                        }

                        var raw = RenderTexture.GetTemporary(
                            view.Pixels.width,
                            view.Pixels.height,
                            24,
                            RenderTextureFormat.ARGB32,
                            RenderTextureReadWrite.sRGB
                        );
                        var corrected = RenderTexture.GetTemporary(
                            view.Pixels.width,
                            view.Pixels.height,
                            0,
                            RenderTextureFormat.ARGB32,
                            RenderTextureReadWrite.sRGB
                        );
                        try
                        {
                            view.GrabPixels(raw, view.Region);
                            Graphics.Blit(raw, corrected, captureMaterial);
                            Graphics.CopyTexture(
                                corrected,
                                0,
                                0,
                                0,
                                0,
                                view.Pixels.width,
                                view.Pixels.height,
                                destination,
                                0,
                                0,
                                view.Pixels.x,
                                view.Pixels.y
                            );
                        }
                        finally
                        {
                            RenderTexture.ReleaseTemporary(corrected);
                            RenderTexture.ReleaseTemporary(raw);
                        }
                    }

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
                }
            }

            public override bool IsValid => rootView != null;
        }

        readonly struct GuiViewCapture
        {
            internal GuiViewCapture(
                Object view,
                Rect region,
                RectInt pixels,
                Action<RenderTexture, Rect> grabPixels)
            {
                View = view;
                Region = region;
                Pixels = pixels;
                GrabPixels = grabPixels;
            }

            internal Object View { get; }
            internal Rect Region { get; }
            internal RectInt Pixels { get; }
            internal Action<RenderTexture, Rect> GrabPixels { get; }
        }

        sealed class EditorWindowSource : EditorCaptureSource
        {
            static readonly Material captureMaterial =
                (Material)EditorGUIUtility.LoadRequired(GrabPixelsMaterialPath);

            readonly EditorWindow window;
            readonly RenderTexture source;
            readonly Action<RenderTexture, Rect> grabPixels;
            readonly EditorWindow? previousTab;
            readonly bool ownsWindow;

            internal EditorWindowSource(
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
                source = GpuCapture.CreateStagingTexture(width, height, depth: 24);
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
