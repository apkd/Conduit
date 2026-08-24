#nullable enable

using System;
using System.Text;

namespace Conduit
{
    static partial class ConduitSimpleJson
    {
        internal static string Serialize(JsonDocument document)
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
    }
}
