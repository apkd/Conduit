#nullable enable

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Conduit
{
    static class ConduitProjectIdentity
    {
        const string PipeNamePrefix = "unity-conduit-";
        const int PipeNameMaxLength = 64;
        const int PipeNamePrefixLength = 14;
        const int PipeNameLegacySlugMaxLength = PipeNameMaxLength - PipeNamePrefixLength;
        const int PipeNameSlugMaxLength = 32;
        const ulong PipeNameHashOffset = 14695981039346656037UL;
        const ulong PipeNameHashPrime = 1099511628211UL;

        public static string GetProjectPath()
            => NormalizeProjectPath(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));

        public static string GetPipeName()
            => GetPipeName(GetProjectPath());

        public static string GetPipeName(string projectPath)
        {
            var normalizedPath = NormalizeProjectPath(projectPath);
            if (normalizedPath is not { Length: > 0 })
                return "unity-conduit-unknown";

            var slug = CreatePipeNameSlug(normalizedPath, PipeNameLegacySlugMaxLength + 1);
            if (slug.Length > 0 && slug.Length <= PipeNameLegacySlugMaxLength)
                return PipeNamePrefix + slug;

            if (slug.Length > PipeNameSlugMaxLength)
                slug = TrimTrailingSeparator(slug[..PipeNameSlugMaxLength]);

            var hash = CreatePipeNameHash(normalizedPath);
            return slug.Length == 0
                ? PipeNamePrefix + hash
                : $"{PipeNamePrefix}{slug}-{hash}";
        }

        static string CreatePipeNameSlug(string normalizedPath, int maxLength)
        {
            var builder = new StringBuilder(Math.Min(normalizedPath.Length, maxLength));
            var previousWasSeparator = false;

            foreach (var character in normalizedPath)
            {
                if (builder.Length >= maxLength)
                    break;

                if (IsAsciiLetterOrDigit(character))
                {
                    builder.Append(ToLowerAscii(character));
                    previousWasSeparator = false;
                    continue;
                }

                if (previousWasSeparator || builder.Length == 0)
                    continue;

                builder.Append('_');
                previousWasSeparator = true;
            }

            if (builder.Length > 0 && builder[builder.Length - 1] == '_')
                builder.Length--;

            return builder.ToString();
        }

        static string CreatePipeNameHash(string normalizedPath)
        {
            var hash = PipeNameHashOffset;

            foreach (var character in normalizedPath)
            {
                hash ^= ToLowerAscii(character);
                hash *= PipeNameHashPrime;
            }

            return hash.ToString("x16");
        }

        static string TrimTrailingSeparator(string value)
        {
            return value.Length > 0 && value[value.Length - 1] == '_'
                ? value[..^1]
                : value;
        }

        static bool IsAsciiLetterOrDigit(char character)
        {
            return character >= 'a' && character <= 'z'
                || character >= 'A' && character <= 'Z'
                || character >= '0' && character <= '9';
        }

        static char ToLowerAscii(char character)
        {
            return character >= 'A' && character <= 'Z'
                ? (char)(character + ('a' - 'A'))
                : character;
        }

        public static string NormalizeProjectPath(string path)
        {
            var normalized = path.Trim().Replace('\\', '/');
            const string localhostPrefix = "//wsl.localhost/";
            const string shortPrefix = "//wsl$/";

            var prefixLength = normalized.StartsWith(localhostPrefix, StringComparison.OrdinalIgnoreCase)
                ? localhostPrefix.Length
                : normalized.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase)
                    ? shortPrefix.Length
                    : 0;
            if (prefixLength > 0 && normalized.Length > prefixLength)
            {
                var distroSeparatorIndex = normalized.IndexOf('/', prefixLength);
                if (distroSeparatorIndex >= 0 && distroSeparatorIndex < normalized.Length - 1)
                    normalized = $"/{normalized[(distroSeparatorIndex + 1)..].TrimStart('/')}";
            }

            if (normalized.Length >= 2
                && normalized[1] == ':'
                && char.IsLetter(normalized[0]))
            {
                var remainder = normalized.Length == 2
                    ? string.Empty
                    : normalized[2] == '/'
                        ? normalized[3..]
                        : normalized[2..];
                normalized = remainder.StartsWith("mnt/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(remainder, "mnt", StringComparison.OrdinalIgnoreCase)
                    ? $"/{remainder}"
                    : remainder.Length == 0
                        ? $"/mnt/{char.ToLowerInvariant(normalized[0])}"
                        : $"/mnt/{char.ToLowerInvariant(normalized[0])}/{remainder}";
            }

            while (normalized.Length > 1 && normalized.EndsWith("/", StringComparison.Ordinal))
                normalized = normalized[..^1];

            return normalized;
        }
    }
}
