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
    abstract partial class EditorCaptureSource : IDisposable
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
        internal static async Task<EditorCaptureSource> CreateAsync(string? target)
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
            using var pooledContainers = ConduitPool.GetPooledList<(object rootView, string target)>(
                out var containers
            );
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

            using var pooledViews = ConduitPool.GetPooledList<object>(out var views);
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

            using var pooledSources = ConduitPool.GetPooledList<EditorCaptureSource>(
                out var sources
            );
            if (sources.Capacity < containers.Count)
                sources.Capacity = containers.Count;
            foreach (var container in containers)
                sources.Add(CreateSource(container.rootView, container.target));

            return sources.ToArray();

            static EditorCaptureSource CreateSource(object rootView, string target)
            {
                if (rootView is not Object unityRootView || unityRootView == null)
                    throw new InvalidOperationException($"Unity editor window '{target}' closed during capture.");

                var rootPosition = GetViewPosition(rootView);
                using var pooledViews = ConduitPool.GetPooledList<object>(out var views);
                CollectGuiViews(rootView, views);
                if (views.Count == 0)
                    throw new InvalidOperationException($"Unity editor window '{target}' has no visible GUI views.");

                // GUIView geometry and GrabPixels regions use points; destinations use backing pixels
                // query the view because detached windows may be on a different-DPI monitor
                float scale = GetBackingScaleFactor(views[0]);
                int width = Mathf.Max(1, Mathf.RoundToInt(rootPosition.width * scale));
                int height = Mathf.Max(1, Mathf.RoundToInt(rootPosition.height * scale));
                using var pooledCaptures = ConduitPool.GetPooledList<GuiViewCapture>(
                    out var captures
                );
                if (captures.Capacity < views.Count)
                    captures.Capacity = views.Count;
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

    }
}
