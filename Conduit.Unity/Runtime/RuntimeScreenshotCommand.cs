#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static class RuntimeScreenshotCommand
    {
#if !MODULE_IMAGECONVERSION && !MODULE_SCREENCAPTURE
        const string ScreenshotModuleUnavailableDiagnostic =
            "ERROR: Unity built-in modules `com.unity.modules.imageconversion` and " +
            "`com.unity.modules.screencapture` are not enabled in this project. " +
            "Ask the user for permission to enable the modules so that the `screenshot` tool can be used.";
#elif !MODULE_IMAGECONVERSION
        const string ScreenshotModuleUnavailableDiagnostic =
            "ERROR: Unity built-in module `com.unity.modules.imageconversion` is not enabled in this project. " +
            "Ask the user for permission to enable the module so that the `screenshot` tool can be used.";
#elif !MODULE_SCREENCAPTURE
        const string ScreenshotModuleUnavailableDiagnostic =
            "ERROR: Unity built-in module `com.unity.modules.screencapture` is not enabled in this project. " +
            "Ask the user for permission to enable the module so that the `screenshot` tool can be used.";
#endif
        internal static Task<BridgeCommandResult> ExecuteAsync(
            string? target,
            CancellationToken ct)
        {
#if !(MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE)
            return Task.FromResult(
                new BridgeCommandResult
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = ScreenshotModuleUnavailableDiagnostic,
                }
            );
#else
            return CaptureScreenshotAsync(target, ct);
#endif
        }

#if MODULE_IMAGECONVERSION && MODULE_SCREENCAPTURE
        static async Task<BridgeCommandResult> CaptureScreenshotAsync(
            string? target,
            CancellationToken ct)
        {
            var normalized = target?.Trim() ?? string.Empty;
            if (normalized.Length > 0
                && normalized is not ("game_view" or "player" or "screen")
                && !normalized.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                && !normalized.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                && normalized[0] != '/')
            {
                return new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = $"Screenshot target '{normalized}' is unavailable in a player.",
                };
            }

            if (Application.isBatchMode
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = "Player screenshots require an interactive Unity player with a graphics device.",
                };
            }

            Texture2D texture;
            if (normalized.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith("/", StringComparison.Ordinal))
            {
                var resolved = ConduitRuntimeSearch.ResolveOne(normalized);
                var camera = resolved as Camera ?? resolved.AsGameObject()?.GetComponent<Camera>();
                if (camera == null)
                    throw new InvalidOperationException($"Screenshot target '{normalized}' is not a camera.");
                texture = CaptureCamera(camera);
            }
            else
                texture = await RuntimeBridgeBootstrap.CaptureScreenshotAsync(ct);

            try
            {
                var bytes = texture.EncodeToPNG();
                return new()
                {
                    return_value = "Player image captured.",
                    artifacts = new[]
                    {
                        BridgeArtifact.FromBytes(
                            $"player-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.png",
                            "image/png",
                            bytes
                        ),
                    },
                };
            }
            finally
            {
                Object.Destroy(texture);
            }
        }
#endif

        internal static Texture2D CaptureCamera(Camera camera)
        {
            var width = Math.Max(1, Screen.width);
            var height = Math.Max(1, Screen.height);
            var renderTexture = RenderTexture.GetTemporary(width, height, 24);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); // encoding reads CPU pixels; no GPU upload is needed
                return texture;
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

    }
}
