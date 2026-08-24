#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace Conduit
{
    static class BridgeStringBuilderExtensions
    {
        internal static StringBuilder Trim(this StringBuilder builder)
        {
            builder.TrimEnd();
            var start = 0;
            while (start < builder.Length && char.IsWhiteSpace(builder[start]))
                start++;

            if (start > 0)
                builder.Remove(0, start);

            return builder;
        }

        internal static StringBuilder TrimEnd(this StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
                builder.Length--;

            return builder;
        }

        internal static string ToTrimmedString(this StringBuilder builder)
            => builder.TrimEnd().ToString();

        internal static StringBuilder AppendInvariant(
            this StringBuilder builder,
            int value,
            ReadOnlySpan<char> format = default)
        {
            Span<char> buffer = stackalloc char[64];
            return value.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture)
                ? builder.Append(buffer[..written])
                : builder.Append(value.ToString(format.ToString(), CultureInfo.InvariantCulture));
        }

        internal static StringBuilder AppendInvariant(
            this StringBuilder builder,
            float value,
            ReadOnlySpan<char> format = default)
        {
            Span<char> buffer = stackalloc char[64];
            return value.TryFormat(buffer, out var written, format, CultureInfo.InvariantCulture)
                ? builder.Append(buffer[..written])
                : builder.Append(value.ToString(format.ToString(), CultureInfo.InvariantCulture));
        }
    }
}
