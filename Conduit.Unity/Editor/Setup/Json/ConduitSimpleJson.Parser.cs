#nullable enable

using System;
using System.Globalization;
using System.Text;

namespace Conduit
{
    static partial class ConduitSimpleJson
    {
        sealed class Parser
        {
            readonly string json;
            int index;

            internal Parser(string json) => this.json = json;

            internal JsonValue? ParseValue()
            {
                SkipWhitespace();
                if (index >= json.Length)
                    throw new InvalidOperationException("Unexpected end of JSON.");

                return json[index] switch
                {
                    '{'                      => ParseObject(),
                    '['                      => ParseArray(),
                    '"'                      => new JsonStringValue { Value = ParseString() },
                    't'                      => ParseLiteral("true", new JsonBoolValue { Value = true }),
                    'f'                      => ParseLiteral("false", new JsonBoolValue { Value = false }),
                    'n'                      => ParseLiteral("null", JsonNullValue.Instance),
                    '-' or >= '0' and <= '9' => new JsonNumberValue { Value = ParseNumber() },
                    _ => throw new InvalidOperationException(
                        $"Unexpected JSON token '{json[index]}'."
                    ),
                };
            }

            internal void ExpectEnd()
            {
                SkipWhitespace();
                if (index != json.Length)
                    throw new InvalidOperationException("Unexpected trailing JSON content.");
            }

            JsonObjectValue ParseObject()
            {
                index++;
                var value = new JsonObjectValue();
                SkipWhitespace();
                if (TryConsume('}'))
                    return value;

                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    value.Properties[key] = ParseValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return value;

                    Expect(',');
                    SkipWhitespace();
                    // editor JSONC commonly permits a trailing comma
                    if (TryConsume('}'))
                        return value;
                }
            }

            JsonArrayValue ParseArray()
            {
                index++;
                var value = new JsonArrayValue();
                SkipWhitespace();
                if (TryConsume(']'))
                    return value;

                while (true)
                {
                    value.Items.Add(ParseValue());
                    SkipWhitespace();
                    if (TryConsume(']'))
                        return value;

                    Expect(',');
                    SkipWhitespace();
                    // editor JSONC commonly permits a trailing comma
                    if (TryConsume(']'))
                        return value;
                }
            }

            string ParseString()
            {
                Expect('"');
                var segmentStart = index;
                StringBuilder? builder = null;
                while (index < json.Length)
                {
                    char character = json[index++];
                    if (character == '"')
                    {
                        if (builder == null)
                            return json.Substring(segmentStart, index - segmentStart - 1);

                        builder.Append(json, segmentStart, index - segmentStart - 1);
                        return builder.ToString();
                    }

                    if (character != '\\')
                        continue;

                    if (index >= json.Length)
                        throw new InvalidOperationException("Unexpected end of JSON escape sequence.");

                    builder ??= new();
                    builder.Append(json, segmentStart, index - segmentStart - 1);
                    switch (json[index++])
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
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
                                throw new InvalidOperationException("Unexpected end of JSON unicode escape.");

                            if (!ushort.TryParse(
                                    json.AsSpan(index, 4),
                                    NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture,
                                    out var codeUnit
                                ))
                                throw new InvalidOperationException("Invalid JSON unicode escape sequence.");

                            builder.Append((char)codeUnit);
                            index += 4;
                            break;
                        default:
                            throw new InvalidOperationException("Invalid JSON escape sequence.");
                    }

                    segmentStart = index;
                }

                throw new InvalidOperationException("Unexpected end of JSON string.");
            }

            string ParseNumber()
            {
                int start = index;
                if (json[index] == '-')
                    index++;

                while (index < json.Length && char.IsDigit(json[index]))
                    index++;

                if (index < json.Length && json[index] == '.')
                {
                    index++;
                    while (index < json.Length && char.IsDigit(json[index]))
                        index++;
                }

                if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
                {
                    index++;
                    if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                        index++;

                    while (index < json.Length && char.IsDigit(json[index]))
                        index++;
                }

                return json[start..index];
            }

            JsonValue ParseLiteral(string literal, JsonValue value)
            {
                if (index + literal.Length > json.Length
                    || string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0)
                    throw new InvalidOperationException($"Expected '{literal}'.");

                index += literal.Length;
                return value;
            }

            void SkipWhitespace()
            {
                while (index < json.Length)
                {
                    if (char.IsWhiteSpace(json[index]))
                    {
                        index++;
                        continue;
                    }

                    if (index + 1 >= json.Length || json[index] != '/')
                        return;

                    // editor settings commonly use JSONC even when the file extension is .json
                    if (json[index + 1] == '/')
                    {
                        index += 2;
                        while (index < json.Length && json[index] != '\n')
                            index++;
                        continue;
                    }

                    if (json[index + 1] != '*')
                        return;

                    int commentEnd = json.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        throw new InvalidOperationException("Unterminated JSON comment.");

                    index = commentEnd + 2;
                }
            }

            void Expect(char character)
            {
                SkipWhitespace();
                if (!TryConsume(character))
                    throw new InvalidOperationException($"Expected '{character}'.");
            }

            bool TryConsume(char character)
            {
                if (index >= json.Length || json[index] != character)
                    return false;

                index++;
                return true;
            }
        }
    }
}
