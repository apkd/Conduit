#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ScreenshotTool
    {
        static async Task<string> CaptureLiveEditorSourceAsync(string target, string prefix)
        {
            using var source = await EditorCaptureSource.CreateAsync(target);
            var output = await SaveLiveEditorSourceAsync(source, prefix);
            return $"{output.Prefix} image captured: {output.RelativePath}";
        }

        static async Task<string> CaptureLiveEditorSourceAsync(
            EditorCaptureSource source,
            string prefix)
        {
            using (source)
            {
                var output = await SaveLiveEditorSourceAsync(source, prefix);
                return $"{output.Prefix} image captured: {output.RelativePath}";
            }
        }

        static async Task<ScreenshotOutputPath> SaveLiveEditorSourceAsync(
            EditorCaptureSource source,
            string prefix)
        {
            var staging = GpuCapture.CreateStagingTexture(source.Width, source.Height);
            try
            {
                if (!source.TryCapture(staging, out var diagnostic))
                    throw new InvalidOperationException(diagnostic);

                var output = AllocateOutputPath(
                    ConduitAssetPathUtility.GetProjectRootPath(),
                    prefix
                );
                try
                {
                    await GpuCapture.SavePreparedJpegAsync(staging, output.AbsolutePath);
                    return output;
                }
                catch
                {
                    File.Delete(output.AbsolutePath);
                    throw;
                }
            }
            finally
            {
                staging.Release();
                Object.DestroyImmediate(staging);
            }
        }

    }
}
