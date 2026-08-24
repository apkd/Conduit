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
    abstract partial class EditorCaptureSource : IDisposable
    {
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
               && IsSelectedTab(parent, window);

        static bool IsSelectedTab(object parent, EditorWindow window)
            => ReferenceEquals(GetSelectedTab(parent), window);

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

    }
}
