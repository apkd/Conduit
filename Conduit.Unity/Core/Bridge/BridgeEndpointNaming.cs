#nullable enable

using System;
using System.Text;

namespace Conduit
{
    /// <summary>Creates the stable IPC endpoint name shared by Conduit clients and servers.</summary>
    static class BridgeEndpointNaming
    {
        const string PipeNamePrefix = "unity-conduit-";
        const int PipeNameMaxLength = 64;
        const int PipeNamePrefixLength = 14;
        const int PipeNameLegacySlugMaxLength = PipeNameMaxLength - PipeNamePrefixLength;
        const int PipeNameSlugMaxLength = 32;
        const ulong PipeNameHashOffset = 14695981039346656037UL;
        const ulong PipeNameHashPrime = 1099511628211UL;

        /// <summary>Creates an endpoint name from an already normalized project path.</summary>
        internal static string GetPipeName(string? normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
                return "unity-conduit-unknown";

            var slug = CreateSlug(normalizedPath!, PipeNameLegacySlugMaxLength + 1);
            if (slug.Length > 0 && slug.Length <= PipeNameLegacySlugMaxLength)
                return PipeNamePrefix + slug;

            if (slug.Length > PipeNameSlugMaxLength)
                slug = TrimTrailingSeparator(slug[..PipeNameSlugMaxLength]);

            var hash = CreateHash(normalizedPath!);
            return slug.Length == 0
                ? PipeNamePrefix + hash
                : $"{PipeNamePrefix}{slug}-{hash}";
        }

        static string CreateSlug(string normalizedPath, int maxLength)
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

        static string CreateHash(string normalizedPath)
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
            => value.Length > 0 && value[value.Length - 1] == '_' ? value[..^1] : value;

        static bool IsAsciiLetterOrDigit(char character)
            => character >= 'a' && character <= 'z'
               || character >= 'A' && character <= 'Z'
               || character >= '0' && character <= '9';

        static char ToLowerAscii(char character)
            => character >= 'A' && character <= 'Z'
                ? (char)(character + ('a' - 'A'))
                : character;
    }
}
