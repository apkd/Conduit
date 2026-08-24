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
        sealed class RenderedTextureSource : EditorCaptureSource
        {
            readonly EditorWindow window;
            readonly Func<RenderTexture?> getTexture;
            readonly bool flipVertically;
            readonly object parent;
            readonly EditorWindow? previousTab;
            readonly bool ownsWindow;

            public RenderedTextureSource(
                string target,
                EditorWindow window,
                object parent,
                int width,
                int height,
                Func<RenderTexture?> getTexture,
                bool flipVertically,
                EditorWindow? previousTab,
                bool ownsWindow)
                : base(target, width, height)
            {
                this.window = window;
                this.parent = parent;
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

                if (!IsSelectedTab(parent, window))
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

    }
}
