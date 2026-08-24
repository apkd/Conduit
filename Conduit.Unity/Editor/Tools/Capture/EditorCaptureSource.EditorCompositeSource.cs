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

    }
}
