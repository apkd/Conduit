#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Conduit
{
    static class ConduitSimpleJson
    {
        internal abstract class JsonValue { }

        internal sealed class JsonObjectValue : JsonValue
        {
            public readonly Dictionary<string, JsonValue?> Properties = new(StringComparer.Ordinal);
        }

        sealed class JsonArrayValue : JsonValue
        {
            public readonly List<JsonValue?> Items = new();
        }

        sealed class JsonStringValue : JsonValue
        {
            public string Value = string.Empty;
        }

        sealed class JsonBoolValue : JsonValue
        {
            public bool Value;
        }

        sealed class JsonNumberValue : JsonValue
        {
            public string Value = string.Empty;
        }

        sealed class JsonNullValue : JsonValue
        {
            public static readonly JsonNullValue Instance = new();
        }

        public static JsonDocument ParseObject(string json)
        {
            var parser = new Parser(json);
            var value = parser.ParseValue() as JsonObjectValue
                        ?? throw new InvalidOperationException("JSON root must be an object.");

            parser.ExpectEnd();
            return new() { Root = value };
        }

        public static string Serialize(JsonDocument document)
        {
            var builder = new StringBuilder();
            WriteValue(builder, document.Root, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        public static JsonObject EnsureObject(JsonObject parent, string propertyName)
        {
            if (parent.Object.Properties.TryGetValue(propertyName, out var value) && value is JsonObjectValue objectValue)
                return new() { Object = objectValue };

            objectValue = new();
            parent.Object.Properties[propertyName] = objectValue;
            return new() { Object = objectValue };
        }

        public static JsonObject Root(JsonDocument document) => new() { Object = document.Root };

        public static JsonObject? GetObject(JsonObject? parent, string propertyName)
        {
            if (parent == null)
                return null;

            return parent.Object.Properties.TryGetValue(propertyName, out var value) && value is JsonObjectValue objectValue
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
            for (var index = 0; index < values.Length; index++)
                array.Items.Add(new JsonStringValue { Value = values[index] });

            target.Object.Properties[propertyName] = array;
        }

        public static void Remove(JsonObject target, string propertyName)
            => target.Object.Properties.Remove(propertyName);

        public static string? GetString(JsonObject? target, string propertyName)
            => target?.Object.Properties.TryGetValue(propertyName, out var value) == true && value is JsonStringValue stringValue
                ? stringValue.Value
                : null;

        public static bool? GetBool(JsonObject? target, string propertyName)
            => target?.Object.Properties.TryGetValue(propertyName, out var value) == true && value is JsonBoolValue boolValue
                ? boolValue.Value
                : null;

        public static string? GetFirstString(JsonObject? target, string propertyName)
        {
            if (target?.Object.Properties.TryGetValue(propertyName, out var value) != true || value is not JsonArrayValue array || array.Items.Count == 0)
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
            var first = true;
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
            for (var index = 0; index < value.Items.Count; index++)
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
            for (var index = 0; index < value.Length; index++)
            {
                switch (value[index])
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
                        if (char.IsControl(value[index]))
                            builder.Append("\\u").Append(((int)value[index]).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(value[index]);

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
                    _                        => throw new InvalidOperationException($"Unexpected JSON token '{json[index]}'."),
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
                    var key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    value.Properties[key] = ParseValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return value;

                    Expect(',');
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
                }
            }

            string ParseString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (character == '"')
                        return builder.ToString();

                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (index >= json.Length)
                        throw new InvalidOperationException("Unexpected end of JSON escape sequence.");

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

                            builder.Append((char)int.Parse(json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default:
                            throw new InvalidOperationException("Invalid JSON escape sequence.");
                    }
                }

                throw new InvalidOperationException("Unexpected end of JSON string.");
            }

            string ParseNumber()
            {
                var start = index;
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
                if (index + literal.Length > json.Length || !string.Equals(json.Substring(index, literal.Length), literal, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Expected '{literal}'.");

                index += literal.Length;
                return value;
            }

            void SkipWhitespace()
            {
                while (index < json.Length && char.IsWhiteSpace(json[index]))
                    index++;
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
