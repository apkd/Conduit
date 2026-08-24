#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace Conduit
{
    static class JsonSyntaxReader
    {
        internal static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
                index++;
        }

        internal static bool TryConsume(string json, ref int index, char expected)
        {
            if (index >= json.Length || json[index] != expected)
                return false;

            index++;
            return true;
        }

        internal static bool TryReadJsonString(string json, ref int index, out string value)
        {
            value = string.Empty;
            if (!TryConsume(json, ref index, '"'))
                return false;

            var start = index;
            BridgeStringBuilderPool.StringBuilderHandle pooledBuilder = default;
            StringBuilder? builder = null;
            try
            {
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (character == '"')
                    {
                        value = builder == null
                            ? json.Substring(start, index - start - 1)
                            : builder.ToString();
                        return true;
                    }

                    if (character != '\\')
                    {
                        builder?.Append(character);
                        continue;
                    }

                    if (builder == null)
                    {
                        pooledBuilder = ConduitPool.GetStringBuilder(out builder);
                        builder.Append(json, start, index - start - 1);
                    }

                    if (index >= json.Length)
                        return false;

                    var escape = json[index++];
                    switch (escape)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            builder.Append(escape);
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (index + 4 > json.Length)
                                return false;

                            if (!ushort.TryParse(json.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit))
                                return false;

                            builder.Append((char)codeUnit);
                            index += 4;
                            break;
                        default:
                            return false;
                    }
                }

                return false;
            }
            finally
            {
                pooledBuilder.Dispose();
            }
        }

        internal static bool TrySkipJsonValue(string json, ref int index)
        {
            if (index >= json.Length)
                return false;

            return json[index] switch
            {
                '{' => TrySkipJsonObject(json, ref index),
                '[' => TrySkipJsonArray(json, ref index),
                '"' => TrySkipJsonString(json, ref index),
                _   => TrySkipJsonPrimitive(json, ref index),
            };
        }

        static bool TrySkipJsonObject(string json, ref int index)
        {
            if (!TryConsume(json, ref index, '{'))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, '}'))
                return true;

            while (index < json.Length)
            {
                if (!TryReadJsonString(json, ref index, out _))
                    return false;

                SkipWhitespace(json, ref index);
                if (!TryConsume(json, ref index, ':'))
                    return false;

                SkipWhitespace(json, ref index);
                if (!TrySkipJsonValue(json, ref index))
                    return false;

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, '}'))
                    return true;

                if (!TryConsume(json, ref index, ','))
                    return false;

                SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TrySkipJsonArray(string json, ref int index)
        {
            if (!TryConsume(json, ref index, '['))
                return false;

            SkipWhitespace(json, ref index);
            if (TryConsume(json, ref index, ']'))
                return true;

            while (index < json.Length)
            {
                if (!TrySkipJsonValue(json, ref index))
                    return false;

                SkipWhitespace(json, ref index);
                if (TryConsume(json, ref index, ']'))
                    return true;

                if (!TryConsume(json, ref index, ','))
                    return false;

                SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TrySkipJsonString(string json, ref int index)
            => TryReadJsonString(json, ref index, out _);

        static bool TrySkipJsonPrimitive(string json, ref int index)
        {
            var start = index;
            while (index < json.Length && !char.IsWhiteSpace(json[index]) && json[index] is not ',' and not ']' and not '}')
                index++;

            return index > start;
        }
    }
}
