#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace Conduit
{
    static class ConduitAssetPathUtility
    {
        static readonly string projectRootPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..")
        );
        static readonly string assetsRootPath = NormalizeFullPath(Path.Combine(projectRootPath, "Assets"));
        static readonly string packagesRootPath = NormalizeFullPath(Path.Combine(projectRootPath, "Packages"));

        public static bool TryResolveAssetPath(string asset, out string assetPath)
        {
            var candidate = asset.AsSpan().Trim();
            if (!LooksLikeAssetIdentifier(candidate))
            {
                assetPath = string.Empty;
                return false;
            }

            try
            {
                assetPath = ResolveAssetPath(asset);
                return true;
            }
            catch
            {
                assetPath = string.Empty;
                return false;
            }
        }

        public static string ResolveAssetPath(string asset)
        {
            if (string.IsNullOrWhiteSpace(asset))
                throw new InvalidOperationException("Asset identifier was empty.");

            var normalizedInput = NormalizeInput(asset);
            if (ConduitUtility.IsLikelyGuid(normalizedInput.AsSpan()))
                normalizedInput = AssetDatabase.GUIDToAssetPath(normalizedInput);

            if (string.IsNullOrWhiteSpace(normalizedInput))
                throw new InvalidOperationException($"Could not resolve asset '{asset}'.");

            if (TryConvertAbsolutePath(normalizedInput, out var absoluteAssetPath))
                normalizedInput = absoluteAssetPath;

            if (TryResolveExistingPath(normalizedInput, out var resolvedAssetPath))
                return resolvedAssetPath;

            throw new InvalidOperationException($"Asset '{asset}' does not exist in the Unity project.");
        }

        public static string[] ExpandAssetPaths(string assetPattern, params string[] allowedExtensions)
        {
            if (string.IsNullOrWhiteSpace(assetPattern))
                throw new InvalidOperationException("Asset pattern was empty.");

            var normalizedExtensions = ConduitUtility.NormalizeExtensions(allowedExtensions);
            var normalizedPattern = NormalizeInput(assetPattern);

            if (!ConduitUtility.ContainsWildcard(normalizedPattern.AsSpan()))
            {
                var resolvedAssetPath = ResolveAssetPath(normalizedPattern);
                if (IsDirectory(resolvedAssetPath))
                    return EnumerateDirectoryAssets(resolvedAssetPath, normalizedExtensions);

                if (normalizedExtensions.Length > 0)
                    ConduitUtility.ValidateExtension(resolvedAssetPath, normalizedExtensions);

                return new[] { resolvedAssetPath };
            }

            if (TryConvertAbsolutePath(normalizedPattern, out var absolutePattern))
                normalizedPattern = absolutePattern;

            return EnumerateWildcardAssets(normalizedPattern, normalizedExtensions);
        }

        public static string GetProjectRootPath()
            => projectRootPath;

        public static string AssetPathToAbsolutePath(string assetPath)
        {
            var projectRootPath = GetProjectRootPath();
            return AssetPathToAbsolutePath(assetPath, projectRootPath);
        }

        static string[] EnumerateDirectoryAssets(string directoryAssetPath, string[] normalizedExtensions)
        {
            var projectRootPath = GetProjectRootPath();
            var directoryPath = AssetPathToAbsolutePath(directoryAssetPath, projectRootPath);
            if (!Directory.Exists(directoryPath))
                throw new InvalidOperationException($"Directory '{directoryAssetPath}' does not exist in the Unity project.");

            var pathMappings = CreatePathMappings(directoryAssetPath);
            using var pooledAssets = ConduitUtility.GetPooledSet<string>(out var assets);
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                if (normalizedExtensions.Length > 0
                    && !ConduitUtility.ContainsExtension(
                        normalizedExtensions,
                        GetExtension(filePath.AsSpan())
                    ))
                    continue;

                var assetPath = ConvertNormalizedAbsoluteToAssetPath(filePath, pathMappings);
                if (assetPath == null)
                    continue;

                assets.Add(assetPath);
            }

            return ConduitUtility.SortStrings(assets, StringComparer.OrdinalIgnoreCase);
        }

        static string[] EnumerateWildcardAssets(string assetPattern, string[] normalizedExtensions)
        {
            var projectRootPath = GetProjectRootPath();
            var pathMappings = CreatePathMappings(assetPattern);
            var assetRegex = new Regex(
                BuildWildcardRegex(assetPattern),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );

            var searchRootAssetPath = GetSearchRootAssetPath(assetPattern);
            using var pooledAssets = ConduitUtility.GetPooledSet<string>(out var assets);
            if (searchRootAssetPath is not { Length: > 0 })
            {
                // only Assets and Packages can map back to Unity asset paths; Library dominates broad project scans.
                ScanDirectory(assetsRootPath);
                ScanDirectory(packagesRootPath);
            }
            else
                ScanDirectory(AssetPathToAbsolutePath(searchRootAssetPath, projectRootPath));

            return ConduitUtility.SortStrings(assets, StringComparer.OrdinalIgnoreCase);

            void ScanDirectory(string searchRootPath)
            {
                if (!Directory.Exists(searchRootPath))
                    return;

                foreach (var filePath in Directory.EnumerateFiles(searchRootPath, "*", SearchOption.AllDirectories))
                {
                    if (normalizedExtensions.Length > 0
                        && !ConduitUtility.ContainsExtension(
                            normalizedExtensions,
                            GetExtension(filePath.AsSpan())
                        ))
                        continue;

                    var assetPath = ConvertNormalizedAbsoluteToAssetPath(filePath, pathMappings);
                    if (assetPath == null
                        || !assetRegex.IsMatch(assetPath))
                        continue;

                    assets.Add(assetPath);
                }
            }
        }

        static string GetSearchRootAssetPath(string assetPattern)
        {
            var wildcardIndex = ConduitUtility.FindWildcardIndex(assetPattern.AsSpan());
            if (wildcardIndex < 0)
                return string.Empty;

            var prefix = assetPattern.AsSpan(0, wildcardIndex);
            var separatorIndex = prefix.LastIndexOf('/');
            return separatorIndex < 0 ? string.Empty : prefix[..separatorIndex].TrimEnd('/').ToString();
        }

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

        static string BuildWildcardRegex(string assetPattern)
        {
            var escapedPattern = Regex.Escape(assetPattern)
                .Replace("\\*\\*/", "(?:.*/)?")
                .Replace("\\*\\*", ".*")
                .Replace("\\*", "[^/]*")
                .Replace("\\?", "[^/]");

            return $"^{escapedPattern}$";
        }

        readonly struct PackagePathMapping
        {
            public readonly string AbsoluteRootPath;
            public readonly string AssetRootPath;

            public PackagePathMapping(string absoluteRootPath, string assetRootPath)
            {
                AbsoluteRootPath = absoluteRootPath;
                AssetRootPath = assetRootPath;
            }
        }

        readonly struct PathMappings
        {
            public readonly string AssetsRootPath;
            public readonly string PackagesRootPath;
            public readonly PackagePathMapping? PackagePathMapping;

            public PathMappings(string assetsRootPath, string packagesRootPath, PackagePathMapping? packagePathMapping)
            {
                AssetsRootPath = assetsRootPath;
                PackagesRootPath = packagesRootPath;
                PackagePathMapping = packagePathMapping;
            }
        }

        static string NormalizeInput(string input)
        {
            var trimmed = input.AsSpan().Trim();
            if (trimmed.IndexOf('\\') < 0)
                return trimmed.Length == input.Length ? input : trimmed.ToString();

            return string.Create(
                trimmed.Length,
                input,
                static (result, source) =>
                {
                    var trimmedSource = source.AsSpan().Trim();
                    for (var index = 0; index < trimmedSource.Length; ++index)
                        result[index] = trimmedSource[index] == '\\' ? '/' : trimmedSource[index];
                }
            );
        }

        static string NormalizeFullPath(string path)
            => TrimTrailingDirectorySeparators(Path.GetFullPath(path));

        static string TrimTrailingDirectorySeparators(string path)
        {
            var trimmedLength = path.Length;
            while (trimmedLength > 0 && IsDirectorySeparator(path[trimmedLength - 1]))
                trimmedLength--;

            return trimmedLength == path.Length ? path : path[..trimmedLength];
        }

        static string TrimTrailingAssetSeparators(string path)
        {
            var trimmedLength = path.Length;
            while (trimmedLength > 0 && path[trimmedLength - 1] == '/')
                trimmedLength--;

            return trimmedLength == path.Length ? path : path[..trimmedLength];
        }

        static bool IsDirectorySeparator(char character)
            => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;

        static ReadOnlySpan<char> GetExtension(ReadOnlySpan<char> path)
        {
            for (var index = path.Length - 1; index >= 0; --index)
            {
                var character = path[index];
                if (character == '.')
                    return index == path.Length - 1 ? ReadOnlySpan<char>.Empty : path[index..];
                if (IsDirectorySeparator(character))
                    break;
            }

            return ReadOnlySpan<char>.Empty;
        }

        static bool IsAssetRelativePath(ReadOnlySpan<char> input)
            => input.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
               || input.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
               || input.Equals("Assets", StringComparison.OrdinalIgnoreCase)
               || input.Equals("Packages", StringComparison.OrdinalIgnoreCase);

        static bool LooksLikeAssetIdentifier(ReadOnlySpan<char> candidate)
        {
            if (ConduitUtility.IsLikelyGuid(candidate)
                || IsAssetRelativeIdentifier(candidate))
                return true;

            if (candidate.StartsWith("./", StringComparison.Ordinal)
                || candidate.StartsWith(".\\", StringComparison.Ordinal))
                return IsAssetRelativeIdentifier(candidate[2..]);

            return candidate.StartsWith("/", StringComparison.Ordinal)
                   || candidate.StartsWith("\\\\", StringComparison.Ordinal)
                   || candidate.Length >= 3
                   && candidate[1] == ':'
                   && (candidate[2] == '/' || candidate[2] == '\\');
        }

        static bool IsAssetRelativeIdentifier(ReadOnlySpan<char> candidate)
            => IsAssetRelativePath(candidate)
               || candidate.StartsWith("Assets\\", StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith("Packages\\", StringComparison.OrdinalIgnoreCase);
    }
}
