#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class ConduitPatternUtility
    {
        internal static int FindWildcardIndex(ReadOnlySpan<char> value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '*' || character == '?')
                    return index;
            }

            return -1;
        }

        /// <summary>
        /// Determines whether a path or search pattern contains wildcard characters.
        /// </summary>
        internal static bool ContainsWildcard(ReadOnlySpan<char> value)
            => FindWildcardIndex(value) >= 0;

        /// <summary>
        /// Normalizes and de-duplicates a set of file extensions.
        /// </summary>
        internal static string[] NormalizeExtensions(string[] allowedExtensions)
        {
            using var pooledList = ConduitPool.GetPooledList<string>(out var normalized);
            using var pooledSet = ConduitPool.GetPooledSet<string>(out var seen);
            foreach (var extension in allowedExtensions)
            {
                var normalizedExtension = NormalizeExtension(extension);
                if (seen.Add(normalizedExtension))
                    normalized.Add(normalizedExtension);
            }

            return normalized.ToArray();
        }

        /// <summary>
        /// Checks extension membership using ordinal-ignore-case comparison.
        /// </summary>
        internal static bool ContainsExtension(IReadOnlyList<string> normalizedExtensions, string extension)
            => ContainsExtension(normalizedExtensions, extension.AsSpan());

        /// <summary>
        /// Checks span-based extension membership without materializing a substring.
        /// </summary>
        internal static bool ContainsExtension(
            IReadOnlyList<string> normalizedExtensions,
            ReadOnlySpan<char> extension)
        {
            for (var index = 0; index < normalizedExtensions.Count; index++)
                if (normalizedExtensions[index].AsSpan().Equals(
                        extension,
                        StringComparison.OrdinalIgnoreCase
                    ))
                    return true;

            return false;
        }

        /// <summary>
        /// Throws when an asset path does not use one of the supported extensions.
        /// </summary>
        internal static void ValidateExtension(string assetPath, IReadOnlyCollection<string> normalizedExtensions)
        {
            if (normalizedExtensions.Count == 0)
                return;

            var extension = System.IO.Path.GetExtension(assetPath);
            foreach (var candidate in normalizedExtensions)
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                    return;

            throw new InvalidOperationException(
                $"Asset '{assetPath}' does not match the supported extensions: {string.Join(", ", normalizedExtensions)}."
            );
        }

        /// <summary>
        /// Detects 32-character hexadecimal GUID strings without allocating.
        /// </summary>
        internal static bool IsLikelyGuid(ReadOnlySpan<char> value)
        {
            if (value.Length != 32)
                return false;

            foreach (var character in value)
                if (character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')
                    and not (>= 'A' and <= 'F'))
                    return false;

            return true;
        }

        /// <summary>
        /// Copies a hash set into a deterministically sorted string array.
        /// </summary>
        internal static string[] SortStrings(HashSet<string> values, StringComparer comparer)
        {
            if (values.Count == 0)
                return Array.Empty<string>();

            var sorted = new string[values.Count];
            var index = 0;
            foreach (var value in values)
                sorted[index++] = value;

            Array.Sort(sorted, comparer);
            return sorted;
        }

        /// <summary>Ensures extensions include a leading dot.</summary>
        static string NormalizeExtension(string extension)
            => extension.StartsWith(".", StringComparison.Ordinal) ? extension : $".{extension}";
    }
}
