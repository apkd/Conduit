#nullable enable

using System;
using System.IO;
using UnityEngine;

namespace Conduit
{
    static class ConduitProjectIdentity
    {
        public static string GetProjectPath()
            => NormalizeProjectPath(Path.GetFullPath(Path.Combine(Application.dataPath, "..")));

        public static string GetPipeName()
            => GetPipeName(GetProjectPath());

        public static string GetPipeName(string projectPath)
        {
            var normalizedPath = NormalizeProjectPath(projectPath);
            if (normalizedPath is not { Length: > 0 })
                return "unity-conduit-unknown";

            var buffer = new char[normalizedPath.Length];
            var count = 0;
            var previousWasSeparator = false;

            foreach (var character in normalizedPath)
            {
                if (char.IsLetterOrDigit(character))
                {
                    buffer[count++] = char.ToLowerInvariant(character);
                    previousWasSeparator = false;
                    continue;
                }

                if (previousWasSeparator)
                    continue;

                buffer[count++] = '_';
                previousWasSeparator = true;
            }

            var start = 0;
            while (start < count && buffer[start] == '_')
                start++;

            while (count > start && buffer[count - 1] == '_')
                count--;

            return count == start
                ? "unity-conduit-unknown"
                : $"unity-conduit-{new string(buffer, start, count - start)}";
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
