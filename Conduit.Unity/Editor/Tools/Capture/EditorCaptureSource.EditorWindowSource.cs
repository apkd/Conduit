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
        sealed class EditorWindowSource : EditorCaptureSource
        {
            static readonly Material captureMaterial =
                (Material)EditorGUIUtility.LoadRequired(GrabPixelsMaterialPath);

            readonly EditorWindow window;
            readonly object parent;
            readonly Action<RenderTexture, Rect> grabPixels;
            readonly EditorWindow? previousTab;
            readonly bool ownsWindow;
            readonly RenderTexture grabTarget;

            internal EditorWindowSource(
                string target,
                EditorWindow window,
                object parent,
                int width,
                int height,
                Action<RenderTexture, Rect> grabPixels,
                EditorWindow? previousTab,
                bool ownsWindow)
                : base(target, width, height)
            {
                this.window = window;
                this.parent = parent;
                this.grabPixels = grabPixels;
                this.previousTab = previousTab;
                this.ownsWindow = ownsWindow;
                grabTarget = GpuCapture.CreateStagingTexture(width, height, depth: 24);
            }

            public override bool TryCapture(RenderTexture destination, out string diagnostic)
            {
                if (window == null)
                {
                    diagnostic = $"The editor window '{Target}' was closed during recording.";
                    return false;
                }

                if (!IsSelectedTab(parent, window))
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
                try
                {
                    // unity's wrapper makes this same backing-buffer capture conditional on window focus
                    grabPixels(grabTarget, new(0f, 0f, window.position.width, window.position.height));
                    Graphics.Blit(grabTarget, destination, captureMaterial);
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

            public override bool IsValid => window != null;

            public override void Dispose()
            {
                try
                {
                    grabTarget.Release();
                    Object.DestroyImmediate(grabTarget);
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
                using var pooledSelectedTabs = ConduitPool.GetPooledList<(EditorWindow, object)>(
                    out var selectedTabs
                );
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
