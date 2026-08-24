#nullable enable

using System;
using System.IO;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Conduit
{
    static partial class ConduitAssetPathUtility
    {
        static bool TryResolveExistingPath(string normalizedInput, out string assetPath)
        {
            assetPath = normalizedInput;
            if (IsAssetRelativePath(normalizedInput.AsSpan()))
            {
                var absolutePath = AssetPathToAbsolutePath(normalizedInput, GetProjectRootPath());
                if (File.Exists(absolutePath) || Directory.Exists(absolutePath))
                    return true;
            }

            var projectRootPath = GetProjectRootPath();
            var pathMappings = CreatePathMappings(normalizedInput);
            var absoluteCandidatePath = Path.GetFullPath(Path.Combine(projectRootPath, normalizedInput.Replace('/', Path.DirectorySeparatorChar)));
            var convertedAssetPath = ConvertAbsoluteToAssetPath(absoluteCandidatePath, pathMappings);
            if (convertedAssetPath == null)
                return false;

            if (!File.Exists(absoluteCandidatePath) && !Directory.Exists(absoluteCandidatePath))
                return false;

            assetPath = convertedAssetPath;
            return true;
        }

        static bool TryConvertAbsolutePath(string path, out string assetPath)
        {
            var projectRootPath = GetProjectRootPath();
            assetPath = ConvertAbsoluteToAssetPath(path, CreatePathMappings()) ?? string.Empty;
            return assetPath.Length > 0;
        }

        static string AssetPathToAbsolutePath(string assetPath, string projectRootPath)
        {
            var normalizedAssetPath = NormalizeInput(assetPath);
            return Path.GetFullPath(Path.Combine(projectRootPath, normalizedAssetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static string? ConvertAbsoluteToAssetPath(string absolutePath, in PathMappings pathMappings)
            => ConvertNormalizedAbsoluteToAssetPath(
                NormalizeFullPath(absolutePath),
                pathMappings
            );

        static string? ConvertNormalizedAbsoluteToAssetPath(
            string normalizedAbsolutePath,
            in PathMappings pathMappings)
        {
            if (StartsWithPath(normalizedAbsolutePath, pathMappings.AssetsRootPath))
                return JoinAssetPath(
                    "Assets",
                    normalizedAbsolutePath,
                    pathMappings.AssetsRootPath.Length
                );

            if (StartsWithPath(normalizedAbsolutePath, pathMappings.PackagesRootPath))
                return JoinAssetPath(
                    "Packages",
                    normalizedAbsolutePath,
                    pathMappings.PackagesRootPath.Length
                );

            if (pathMappings.PackagePathMapping is { } mapping)
                if (StartsWithPath(normalizedAbsolutePath, mapping.AbsoluteRootPath))
                    return JoinAssetPath(
                        mapping.AssetRootPath,
                        normalizedAbsolutePath,
                        mapping.AbsoluteRootPath.Length
                    );

            return null;
        }

        static string JoinAssetPath(string root, string absolutePath, int suffixOffset)
            => string.Create(
                root.Length + absolutePath.Length - suffixOffset,
                (root, absolutePath, suffixOffset),
                static (result, state) =>
                {
                    state.root.AsSpan().CopyTo(result);
                    var outputIndex = state.root.Length;
                    for (var inputIndex = state.suffixOffset;
                         inputIndex < state.absolutePath.Length;
                         ++inputIndex)
                        result[outputIndex++] = IsDirectorySeparator(state.absolutePath[inputIndex])
                            ? '/'
                            : state.absolutePath[inputIndex];
                }
            );

        static PathMappings CreatePathMappings(string? assetPath = null)
            => new(
                assetsRootPath,
                packagesRootPath,
                assetPath is { Length: > 0 } ? GetPackagePathMapping(assetPath) : null);

        static PackagePathMapping? GetPackagePathMapping(string assetPath)
        {
            if (!IsPackageRelativePath(assetPath.AsSpan()))
                return null;

            var packageInfo = PackageInfo.FindForAssetPath(assetPath);
            if (packageInfo == null
                || string.IsNullOrWhiteSpace(packageInfo.assetPath)
                || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                return null;

            return new(
                NormalizeFullPath(packageInfo.resolvedPath),
                TrimTrailingAssetSeparators(NormalizeInput(packageInfo.assetPath)));
        }

        static bool StartsWithPath(ReadOnlySpan<char> candidatePath, ReadOnlySpan<char> rootPath)
        {
            if (!candidatePath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                return false;

            return candidatePath.Length == rootPath.Length
                   || rootPath.Length < candidatePath.Length && IsDirectorySeparator(candidatePath[rootPath.Length]);
        }

        static bool IsDirectory(string assetPath)
            => Directory.Exists(AssetPathToAbsolutePath(assetPath));

        static bool IsPackageRelativePath(ReadOnlySpan<char> assetPath)
            => assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

    }
}
