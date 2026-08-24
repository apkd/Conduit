#nullable enable

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitAssetPathUtility
    {
        static readonly string projectRootPath = Path.GetFullPath(
            Path.Combine(Application.dataPath, "..")
        );
        static readonly string assetsRootPath = NormalizeFullPath(Path.Combine(projectRootPath, "Assets"));
        static readonly string packagesRootPath = NormalizeFullPath(Path.Combine(projectRootPath, "Packages"));

        internal static bool TryResolveAssetPath(string asset, out string assetPath)
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

        internal static string ResolveAssetPath(string asset)
        {
            if (string.IsNullOrWhiteSpace(asset))
                throw new InvalidOperationException("Asset identifier was empty.");

            var normalizedInput = NormalizeInput(asset);
            if (ConduitPatternUtility.IsLikelyGuid(normalizedInput.AsSpan()))
                normalizedInput = AssetDatabase.GUIDToAssetPath(normalizedInput);

            if (string.IsNullOrWhiteSpace(normalizedInput))
                throw new InvalidOperationException($"Could not resolve asset '{asset}'.");

            if (TryConvertAbsolutePath(normalizedInput, out var absoluteAssetPath))
                normalizedInput = absoluteAssetPath;

            if (TryResolveExistingPath(normalizedInput, out var resolvedAssetPath))
                return resolvedAssetPath;

            throw new InvalidOperationException($"Asset '{asset}' does not exist in the Unity project.");
        }

        internal static string[] ExpandAssetPaths(string assetPattern, params string[] allowedExtensions)
        {
            if (string.IsNullOrWhiteSpace(assetPattern))
                throw new InvalidOperationException("Asset pattern was empty.");

            var normalizedExtensions = ConduitPatternUtility.NormalizeExtensions(allowedExtensions);
            var normalizedPattern = NormalizeInput(assetPattern);

            if (!ConduitPatternUtility.ContainsWildcard(normalizedPattern.AsSpan()))
            {
                var resolvedAssetPath = ResolveAssetPath(normalizedPattern);
                if (IsDirectory(resolvedAssetPath))
                    return EnumerateDirectoryAssets(resolvedAssetPath, normalizedExtensions);

                if (normalizedExtensions.Length > 0)
                    ConduitPatternUtility.ValidateExtension(resolvedAssetPath, normalizedExtensions);

                return new[] { resolvedAssetPath };
            }

            if (TryConvertAbsolutePath(normalizedPattern, out var absolutePattern))
                normalizedPattern = absolutePattern;

            return EnumerateWildcardAssets(normalizedPattern, normalizedExtensions);
        }

        internal static string GetProjectRootPath()
            => projectRootPath;

        internal static string AssetPathToAbsolutePath(string assetPath)
        {
            var projectRootPath = GetProjectRootPath();
            return AssetPathToAbsolutePath(assetPath, projectRootPath);
        }

    }
}
