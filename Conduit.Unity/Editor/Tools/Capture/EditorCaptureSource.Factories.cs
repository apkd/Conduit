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
                var parent = GetParent(window)
                             ?? throw new InvalidOperationException(
                                 $"Editor window '{window.titleContent.text}' has no host view."
                             );
                return new RenderedTextureSource(
                    target,
                    window,
                    parent,
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
                    parent,
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

    }
}
