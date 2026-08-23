#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Conduit
{
    // built-in Unity JSON APIs cannot round-trip arbitrary editor settings
    // this DOM retains unknown values and ordering
    static class ConduitSimpleJson
    {
        internal abstract class JsonValue { }

        internal sealed class JsonObjectValue : JsonValue
        {
            internal Dictionary<string, JsonValue?> Properties { get; } = new(StringComparer.Ordinal);
        }

        internal sealed class JsonArrayValue : JsonValue
        {
            internal List<JsonValue?> Items { get; } = new();
        }

        internal sealed class JsonStringValue : JsonValue
        {
            internal string Value = string.Empty;
        }

        internal sealed class JsonBoolValue : JsonValue
        {
            internal bool Value;
        }

        internal sealed class JsonNumberValue : JsonValue
        {
            // preserve source spelling and precision for unrelated numeric settings
            internal string Value = string.Empty;
        }

        internal sealed class JsonNullValue : JsonValue
        {
            internal static readonly JsonNullValue Instance = new();
        }

        public static JsonDocument ParseObject(string json)
        {
            var parser = new Parser(json);
            if (parser.ParseValue() is not JsonObjectValue value)
                throw new InvalidOperationException("JSON root must be an object.");

            parser.ExpectEnd();
            return new() { Root = value };
        }

        internal static JsonValue? ParseValue(string json)
        {
            var parser = new Parser(json);
            var value = parser.ParseValue();
            parser.ExpectEnd();
            return value;
        }

        public static string Serialize(JsonDocument document)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            WriteValue(builder, document.Root, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        internal static string SerializeValue(JsonValue? value)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            WriteValue(builder, value, 0);
            return builder.ToString();
        }

        internal static string Quote(string value)
        {
            if (!RequiresEscaping(value))
                return string.Concat("\"", value, "\"");

            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                value.Length + 2
            );
            WriteString(builder, value);
            return builder.ToString();

            static bool RequiresEscaping(string text)
            {
                foreach (var character in text)
                    if (character is '\\' or '"' || char.IsControl(character))
                        return true;

                return false;
            }
        }

        internal static void AppendQuoted(StringBuilder builder, string value) => WriteString(builder, value);

        public static bool ContainsComments(string json)
        {
            // reads accept JSONC, but writers use this probe to avoid silently deleting comment trivia
            bool inString = false;
            bool escaped = false;
            for (int index = 0, length = json.Length - 1; index < length; ++index)
            {
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (json[index] == '\\')
                        escaped = true;
                    else if (json[index] == '"')
                        inString = false;
                    continue;
                }

                if (json[index] == '"')
                {
                    inString = true;
                    continue;
                }

                if (json[index] == '/' && json[index + 1] is '/' or '*')
                    return true;
            }

            return false;
        }

        public static JsonObject EnsureObject(JsonObject parent, string propertyName)
        {
            if (parent.Object.Properties.TryGetValue(propertyName, out var value))
            {
                if (value is JsonObjectValue existingObject)
                    return new() { Object = existingObject };

                throw new InvalidOperationException($"JSON property '{propertyName}' must be an object.");
            }

            var objectValue = new JsonObjectValue();
            parent.Object.Properties[propertyName] = objectValue;
            return new() { Object = objectValue };
        }

        public static JsonObject Root(JsonDocument document) => new() { Object = document.Root };

        public static JsonObject? GetObject(JsonObject? parent, string propertyName)
        {
            if (parent is null)
                return null;

            return parent.Object.Properties.TryGetValue(propertyName, out var value)
                   && value is JsonObjectValue objectValue
                ? new JsonObject { Object = objectValue }
                : null;
        }

        public static void SetString(JsonObject target, string propertyName, string value)
            => target.Object.Properties[propertyName] = new JsonStringValue { Value = value };

        public static void SetBool(JsonObject target, string propertyName, bool value)
            => target.Object.Properties[propertyName] = new JsonBoolValue { Value = value };

        public static void SetStringArray(JsonObject target, string propertyName, params string[] values)
        {
            var array = new JsonArrayValue();
            foreach (string value in values)
                array.Items.Add(new JsonStringValue { Value = value });

            target.Object.Properties[propertyName] = array;
        }

        public static void Remove(JsonObject target, string propertyName)
            => target.Object.Properties.Remove(propertyName);

        public static string? GetString(JsonObject? target, string propertyName)
            => target?.Object.Properties.TryGetValue(propertyName, out var value) == true
               && value is JsonStringValue stringValue
                ? stringValue.Value
                : null;

        public static bool? GetBool(JsonObject? target, string propertyName)
            => target?.Object.Properties.TryGetValue(propertyName, out var value) == true
               && value is JsonBoolValue boolValue
                ? boolValue.Value
                : null;

        public static string? GetFirstString(JsonObject? target, string propertyName)
        {
            if (target?.Object.Properties.TryGetValue(propertyName, out var value) != true
                || value is not JsonArrayValue { Items: { Count: > 0 } } array)
                return null;

            return array.Items[0] is JsonStringValue stringValue ? stringValue.Value : null;
        }

        internal sealed class JsonDocument
        {
            internal JsonObjectValue Root = null!;
        }

        internal sealed class JsonObject
        {
            internal JsonObjectValue Object = null!;
        }

        static void WriteValue(StringBuilder builder, JsonValue? value, int indent)
        {
            switch (value)
            {
                case JsonObjectValue objectValue:
                    WriteObject(builder, objectValue, indent);
                    return;
                case JsonArrayValue arrayValue:
                    WriteArray(builder, arrayValue, indent);
                    return;
                case JsonStringValue stringValue:
                    WriteString(builder, stringValue.Value);
                    return;
                case JsonBoolValue boolValue:
                    builder.Append(boolValue.Value ? "true" : "false");
                    return;
                case JsonNumberValue numberValue:
                    builder.Append(numberValue.Value);
                    return;
                case null:
                case JsonNullValue:
                    builder.Append("null");
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported JSON value '{value.GetType().Name}'.");
            }
        }

        static void WriteObject(StringBuilder builder, JsonObjectValue value, int indent)
        {
            builder.Append('{');
            if (value.Properties.Count == 0)
            {
                builder.Append('}');
                return;
            }

            builder.Append('\n');
            bool first = true;
            foreach (var pair in value.Properties)
            {
                if (!first)
                    builder.Append(",\n");

                first = false;
                builder.Append(' ', (indent + 1) * 2);
                WriteString(builder, pair.Key);
                builder.Append(": ");
                WriteValue(builder, pair.Value, indent + 1);
            }

            builder.Append('\n');
            builder.Append(' ', indent * 2);
            builder.Append('}');
        }

        static void WriteArray(StringBuilder builder, JsonArrayValue value, int indent)
        {
            builder.Append('[');
            if (value.Items.Count == 0)
            {
                builder.Append(']');
                return;
            }

            builder.Append('\n');
            for (int index = 0, count = value.Items.Count; index < count; ++index)
            {
                if (index > 0)
                    builder.Append(",\n");

                builder.Append(' ', (indent + 1) * 2);
                WriteValue(builder, value.Items[index], indent + 1);
            }

            builder.Append('\n');
            builder.Append(' ', indent * 2);
            builder.Append(']');
        }

        static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                    {
                        if (char.IsControl(character))
                            builder.Append("\\u")
                                .AppendInvariant((int)character, "x4");
                        else
                            builder.Append(character);

                        break;
                    }
                }
            }

            builder.Append('"');
        }

        sealed class Parser
        {
            readonly string json;
            int index;

            public Parser(string json) => this.json = json;

            public JsonValue? ParseValue()
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

            public void ExpectEnd()
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
