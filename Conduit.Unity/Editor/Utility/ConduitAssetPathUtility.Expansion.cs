#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Conduit
{
    static partial class ConduitAssetPathUtility
    {
        static string[] EnumerateDirectoryAssets(string directoryAssetPath, string[] normalizedExtensions)
        {
            var projectRootPath = GetProjectRootPath();
            var directoryPath = AssetPathToAbsolutePath(directoryAssetPath, projectRootPath);
            if (!Directory.Exists(directoryPath))
                throw new InvalidOperationException($"Directory '{directoryAssetPath}' does not exist in the Unity project.");

            var pathMappings = CreatePathMappings(directoryAssetPath);
            using var pooledAssets = ConduitPool.GetPooledSet<string>(out var assets);
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                if (normalizedExtensions.Length > 0
                    && !ConduitPatternUtility.ContainsExtension(
                        normalizedExtensions,
                        GetExtension(filePath.AsSpan())
                    ))
                    continue;

                var assetPath = ConvertNormalizedAbsoluteToAssetPath(filePath, pathMappings);
                if (assetPath == null)
                    continue;

                assets.Add(assetPath);
            }

            return ConduitPatternUtility.SortStrings(assets, StringComparer.OrdinalIgnoreCase);
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
            using var pooledAssets = ConduitPool.GetPooledSet<string>(out var assets);
            if (searchRootAssetPath is not { Length: > 0 })
            {
                // only Assets and Packages can map back to Unity asset paths; Library dominates broad project scans.
                ScanDirectory(assetsRootPath);
                ScanDirectory(packagesRootPath);
            }
            else
                ScanDirectory(AssetPathToAbsolutePath(searchRootAssetPath, projectRootPath));

            return ConduitPatternUtility.SortStrings(assets, StringComparer.OrdinalIgnoreCase);

            void ScanDirectory(string searchRootPath)
            {
                if (!Directory.Exists(searchRootPath))
                    return;

                foreach (var filePath in Directory.EnumerateFiles(searchRootPath, "*", SearchOption.AllDirectories))
                {
                    if (normalizedExtensions.Length > 0
                        && !ConduitPatternUtility.ContainsExtension(
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
            var wildcardIndex = ConduitPatternUtility.FindWildcardIndex(assetPattern.AsSpan());
            if (wildcardIndex < 0)
                return string.Empty;

            var prefix = assetPattern.AsSpan(0, wildcardIndex);
            var separatorIndex = prefix.LastIndexOf('/');
            return separatorIndex < 0 ? string.Empty : prefix[..separatorIndex].TrimEnd('/').ToString();
        }

    }
}
