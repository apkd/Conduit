#nullable enable

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Conduit
{
    static partial class ConduitAssetPathUtility
    {
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
            internal readonly string AbsoluteRootPath;
            internal readonly string AssetRootPath;

            internal PackagePathMapping(string absoluteRootPath, string assetRootPath)
            {
                AbsoluteRootPath = absoluteRootPath;
                AssetRootPath = assetRootPath;
            }
        }

        readonly struct PathMappings
        {
            internal readonly string AssetsRootPath;
            internal readonly string PackagesRootPath;
            internal readonly PackagePathMapping? PackagePathMapping;

            internal PathMappings(string assetsRootPath, string packagesRootPath, PackagePathMapping? packagePathMapping)
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
            if (ConduitPatternUtility.IsLikelyGuid(candidate)
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
