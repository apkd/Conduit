#nullable enable

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Conduit
{
    sealed partial class McpStdioTestClient
    {
        static readonly Regex responseIdRegex = new("\"id\":(?<id>\\d+)", RegexOptions.Compiled);
        static readonly Regex jsonStringPropertyRegex = new("\"(?<name>[^\"]+)\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex toolNameRegex = new("\"name\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex textContentRegex = new("\"type\":\"text\"\\s*,\\s*\"text\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex nestedServerNameRegex = new("\"serverInfo\":\\{[^}]*\"name\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex errorMessageRegex = new("\"error\":\\{[^}]*\"message\":\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
        static readonly Regex isErrorRegex = new("\"isError\":(?<value>true|false)", RegexOptions.Compiled);

        static bool TryGetResponseId(string line, out int responseId)
        {
            responseId = 0;
            var match = responseIdRegex.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out responseId))
                return false;

            return responseId > 0;
        }

        static string SerializeEnvelope(string method, int? id, IReadOnlyDictionary<string, object?> parameters)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            AppendJsonProperty(builder, "jsonrpc", "2.0", first: true);
            if (id.HasValue)
                AppendJsonProperty(builder, "id", id.Value, first: false);

            AppendJsonProperty(builder, "method", method, first: false);
            AppendJsonProperty(builder, "params", parameters, first: false);
            builder.Append('}');
            return builder.ToString();
        }

        static void AppendJsonProperty(StringBuilder builder, string name, object? value, bool first)
        {
            if (!first)
                builder.Append(',');

            builder.Append('"');
            builder.Append(EscapeJson(name));
            builder.Append("\":");
            AppendJsonValue(builder, value);
        }

        static void AppendJsonValue(StringBuilder builder, object? value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    return;
                case string text:
                    builder.Append('"');
                    builder.Append(EscapeJson(text));
                    builder.Append('"');
                    return;
                case bool boolean:
                    builder.Append(boolean ? "true" : "false");
                    return;
                case int number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case long number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case float number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case double number:
                    builder.Append(number.ToString(CultureInfo.InvariantCulture));
                    return;
                case IReadOnlyDictionary<string, object?> dictionary:
                    AppendJsonObject(builder, dictionary);
                    return;
                case IDictionary<string, object?> dictionary:
                    AppendJsonObject(builder, dictionary);
                    return;
                case string[] strings:
                    AppendJsonArray(builder, strings);
                    return;
                case IEnumerable<string> strings:
                    AppendJsonArray(builder, strings);
                    return;
                default:
                    throw new NotSupportedException($"Unsupported MCP JSON value type '{value.GetType().FullName}'.");
            }
        }

        static void AppendJsonObject(StringBuilder builder, IEnumerable<KeyValuePair<string, object?>> values)
        {
            builder.Append('{');
            var first = true;
            foreach (var (key, value) in values)
            {
                AppendJsonProperty(builder, key, value, first);
                first = false;
            }

            builder.Append('}');
        }

        static void AppendJsonArray(StringBuilder builder, IEnumerable<string> values)
        {
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                    builder.Append(',');

                builder.Append('"');
                builder.Append(EscapeJson(value));
                builder.Append('"');
                first = false;
            }

            builder.Append(']');
        }

        static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var builder = new StringBuilder(value.Length + 8);
            foreach (var character in value)
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
                        if (character < ' ')
                            builder.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)character);
                        else
                            builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }

        static bool TryGetErrorMessage(string response, out string message)
        {
            var match = errorMessageRegex.Match(response);
            if (!match.Success)
            {
                message = string.Empty;
                return false;
            }

            message = UnescapeJson(match.Groups["value"].Value);
            return true;
        }

        static bool TryGetStringProperty(string response, string propertyName, out string value)
        {
            foreach (Match match in jsonStringPropertyRegex.Matches(response))
            {
                if (match.Groups["name"].Value != propertyName)
                    continue;

                value = UnescapeJson(match.Groups["value"].Value);
                return true;
            }

            value = string.Empty;
            return false;
        }

        static bool TryGetServerName(string response, out string serverName)
        {
            var match = nestedServerNameRegex.Match(response);
            if (!match.Success)
            {
                serverName = string.Empty;
                return false;
            }

            serverName = UnescapeJson(match.Groups["value"].Value);
            return true;
        }

        static string UnescapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var builder = new StringBuilder(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character != '\\' || index == value.Length - 1)
                {
                    builder.Append(character);
                    continue;
                }

                var escaped = value[++index];
                switch (escaped)
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
                        if (index + 4 >= value.Length)
                            throw new FormatException("Invalid JSON unicode escape sequence.");

                        var codePoint = value.Substring(index + 1, 4);
                        builder.Append((char)int.Parse(codePoint, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
#endif
