#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Conduit
{
    static partial class ScreenshotTool
    {
        static async Task<string> SaveRenderTextureAsync(
            RenderTexture renderTexture,
            string prefix,
            bool flipVertically = false)
        {
            if (flipVertically)
                return await SaveTextureAsync(renderTexture, prefix, flipVertically: true);

#if MODULE_IMAGECONVERSION
            var outputPath = AllocateOutputPath(
                ConduitAssetPathUtility.GetProjectRootPath(),
                prefix
            );
            try
            {
                await GpuCapture.SavePreparedJpegAsync(
                    renderTexture,
                    outputPath.AbsolutePath
                );
                return $"{outputPath.Prefix} image captured: {outputPath.RelativePath}";
            }
            catch
            {
                File.Delete(outputPath.AbsolutePath);
                throw;
            }
#else
            await Task.Yield();
            throw new InvalidOperationException(ModuleUnavailableDiagnostic);
#endif
        }

        static async Task<string> SaveTextureAsync(Texture texture, string prefix, bool flipVertically = false)
        {
#if MODULE_IMAGECONVERSION
            var outputPath = AllocateOutputPath(ConduitAssetPathUtility.GetProjectRootPath(), prefix);
            try
            {
                await GpuCapture.SaveJpegAsync(
                    texture,
                    outputPath.AbsolutePath,
                    flipVertically
                );
                return $"{outputPath.Prefix} image captured: {outputPath.RelativePath}";
            }
            catch
            {
                File.Delete(outputPath.AbsolutePath);
                throw;
            }
#else
            await Task.Yield();
            throw new InvalidOperationException(ModuleUnavailableDiagnostic);
#endif
        }

        internal static ScreenshotOutputPath AllocateOutputPath(string projectPath, string prefix)
        {
            var sanitizedPrefix = SanitizePrefix(prefix);
            var outputDirectoryPath = Path.Combine(projectPath, "Temp", OutputDirectoryName);
            Directory.CreateDirectory(outputDirectoryPath);

            for (var index = FindNextOutputIndex(outputDirectoryPath, sanitizedPrefix);
                 index < int.MaxValue;
                 ++index)
            {
                var fileName = $"{sanitizedPrefix}_{index}.jpg";
                var absolutePath = Path.Combine(outputDirectoryPath, fileName);
                if (File.Exists(absolutePath))
                    continue;

                return new(
                    sanitizedPrefix,
                    $"Temp/{OutputDirectoryName}/{fileName}",
                    absolutePath
                );
            }

            throw new InvalidOperationException($"Could not allocate a screenshot output path for '{sanitizedPrefix}'.");
        }

        static int FindNextOutputIndex(string directory, string prefix)
        {
            var nextIndex = 1;
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "_*.jpg"))
            {
                var fileName = Path.GetFileName(path);
                var numberOffset = prefix.Length + 1;
                if (fileName.Length <= numberOffset + ".jpg".Length
                    || !fileName.AsSpan(0, prefix.Length).Equals(
                        prefix.AsSpan(),
                        StringComparison.Ordinal
                    )
                    || fileName[prefix.Length] != '_'
                    || !fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(
                        fileName.AsSpan(numberOffset, fileName.Length - numberOffset - ".jpg".Length),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var index
                    )
                    || index < nextIndex)
                    continue;

                if (index == int.MaxValue)
                    return 1;

                nextIndex = index + 1;
            }

            return nextIndex;
        }

        static string SanitizePrefix(string prefix)
        {
            var trimmed = prefix?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
                return "capture";

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            bool previousWasUnderscore = false;
            foreach (var character in trimmed)
            {
                if (builder.Length >= 32)
                    break;

                var mappedCharacter = char.IsLetterOrDigit(character) ? character : '_';
                if (mappedCharacter == '_')
                {
                    if (previousWasUnderscore)
                        continue;

                    previousWasUnderscore = true;
                }
                else
                {
                    previousWasUnderscore = false;
                }

                builder.Append(mappedCharacter);
            }

            while (builder.Length > 0 && builder[^1] == '_')
                --builder.Length;
            var leadingUnderscores = 0;
            while (leadingUnderscores < builder.Length && builder[leadingUnderscores] == '_')
                ++leadingUnderscores;
            if (leadingUnderscores > 0)
                builder.Remove(0, leadingUnderscores);

            var sanitized = builder.ToString();
            return sanitized.Length == 0 ? "capture" : sanitized;
        }

        static (int Width, int Height) GetDefaultCaptureSize(float aspect)
        {
            if (!float.IsFinite(aspect) || aspect <= 0f)
                return (DefaultRenderWidth, DefaultRenderHeight);

            var width = DefaultRenderWidth;
            var height = Mathf.Max(1, Mathf.RoundToInt(width / aspect));
            return height <= DefaultRenderHeight * 2
                ? (width, height)
                : (Mathf.Max(1, Mathf.RoundToInt(DefaultRenderHeight * aspect)), DefaultRenderHeight);
        }

        static void EnsureCanRenderScreenshot(string prefix)
        {
            if (Application.isBatchMode)
                throw new InvalidOperationException($"'{prefix}' screenshots require an interactive Unity editor window.");

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                throw new InvalidOperationException($"'{prefix}' screenshots require a graphics device. Unity is running without one.");
        }

        internal readonly struct ScreenshotOutputPath
        {
            internal ScreenshotOutputPath(string prefix, string relativePath, string absolutePath)
            {
                Prefix = prefix;
                RelativePath = relativePath;
                AbsolutePath = absolutePath;
            }

            internal string Prefix { get; }
            internal string RelativePath { get; }
            internal string AbsolutePath { get; }
        }
    }
}
