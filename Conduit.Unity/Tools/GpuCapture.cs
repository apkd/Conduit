#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Stages, reads back, and encodes GPU images without managed pixel copies.</summary>
    static class GpuCapture
    {
        public static RenderTexture CreateStagingTexture(int width, int height)
        {
            var texture = new RenderTexture(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB
            )
            {
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };
            texture.Create();
            return texture;
        }

        public static async Task SaveJpegAsync(
            Texture source,
            string path,
            bool flipVertically,
            int quality = 95)
        {
            var staging = CreateStagingTexture(
                Mathf.Max(1, source.width),
                Mathf.Max(1, source.height)
            );
            var previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(
                    source,
                    staging,
                    flipVertically ? new(1f, -1f) : Vector2.one,
                    flipVertically ? new(0f, 1f) : Vector2.zero
                );
                await SavePreparedJpegAsync(staging, path, quality);
            }
            finally
            {
                RenderTexture.active = previousActive;
                staging.Release();
                Object.DestroyImmediate(staging);
            }
        }

        public static async Task SavePreparedJpegAsync(
            RenderTexture source,
            string path,
            int quality = 95)
        {
#if MODULE_IMAGECONVERSION
            // inactive editors can stop dispatching async readback callbacks until an OS window is focused
            if (!SystemInfo.supportsAsyncGPUReadback || !InternalEditorUtility.isApplicationActive)
            {
                SaveSynchronously(source, path, quality);
                return;
            }

            var pixels = new NativeArray<byte>(
                checked(source.width * source.height * 4),
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );
            try
            {
                var completion = new TaskCompletionSource<AsyncGPUReadbackRequest>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                var request = AsyncGPUReadback.RequestIntoNativeArray(
                    ref pixels,
                    source,
                    0,
                    TextureFormat.RGBA32,
                    request => completion.TrySetResult(request)
                );
                if (await Task.WhenAny(completion.Task, Task.Delay(1000)) == completion.Task)
                    request = await completion.Task;
                else
                    request.WaitForCompletion();

                if (request.hasError)
                    throw new InvalidOperationException("Unity reported an asynchronous GPU readback error.");

                using var encoded = ImageConversion.EncodeNativeArrayToJPG(
                    pixels,
                    GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)source.width,
                    (uint)source.height,
                    0,
                    quality
                );
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(encoded.AsReadOnlySpan());
            }
            finally
            {
                pixels.Dispose();
            }
#else
            await Task.Yield();
            throw new InvalidOperationException(screenshot.ModuleUnavailableDiagnostic);
#endif
        }

#if MODULE_IMAGECONVERSION
        static void SaveSynchronously(RenderTexture source, string path, int quality)
        {
            var previousActive = RenderTexture.active;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new(0f, 0f, source.width, source.height), 0, 0);
                texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                File.WriteAllBytes(path, texture.EncodeToJPG(quality));
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(texture);
            }
        }
#endif
    }
}
