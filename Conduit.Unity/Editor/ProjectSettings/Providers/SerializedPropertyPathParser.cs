#nullable enable

using System;
using System.Globalization;

namespace Conduit
{
    static class SerializedPropertyPathParser
    {
        const string ArrayElementMarker = ".Array.data[";

        internal static bool TryParseMapElement(
            string path,
            out string map,
            out int index,
            out ReadOnlySpan<char> field)
        {
            index = 0;
            field = default;
            int marker = path.IndexOf(ArrayElementMarker, StringComparison.Ordinal);
            if (marker <= 0 || path.AsSpan(0, marker).IndexOf('.') >= 0
                || !TryParseArrayElement(path, marker, out index, out field))
            {
                map = string.Empty;
                return false;
            }

            map = path[..marker];
            return true;
        }

        internal static bool TryParseArrayElement(
            string path,
            ReadOnlySpan<char> arrayPath,
            out int index,
            out ReadOnlySpan<char> field)
        {
            index = 0;
            field = default;
            return path.AsSpan().StartsWith(arrayPath, StringComparison.Ordinal)
                   && TryParseArrayElement(path, arrayPath.Length, out index, out field);
        }

        static bool TryParseArrayElement(
            string path,
            int arrayPathLength,
            out int index,
            out ReadOnlySpan<char> field)
        {
            index = 0;
            var suffix = path.AsSpan(arrayPathLength);
            if (!suffix.StartsWith(ArrayElementMarker, StringComparison.Ordinal))
            {
                index = 0;
                field = default;
                return false;
            }

            int indexStart = arrayPathLength + ArrayElementMarker.Length;
            int indexEnd = path.IndexOf(']', indexStart);
            var indexText = indexEnd < 0
                ? default
                : path.AsSpan(indexStart, indexEnd - indexStart);
            if (indexText.Length == 0
                || !int.TryParse(
                    indexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out index
                ))
            {
                field = default;
                return false;
            }

            int fieldStart = indexEnd + 1;
            if (fieldStart == path.Length)
            {
                field = ReadOnlySpan<char>.Empty;
                return true;
            }

            if (path[fieldStart] != '.' || ++fieldStart == path.Length)
            {
                field = default;
                return false;
            }

            field = path.AsSpan(fieldStart);
            return true;
        }
    }
}
