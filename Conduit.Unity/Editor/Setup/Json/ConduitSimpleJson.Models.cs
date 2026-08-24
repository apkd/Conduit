#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static partial class ConduitSimpleJson
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

        internal readonly struct JsonDocument
        {
            internal JsonDocument(JsonObjectValue root) => Root = root;

            internal JsonObjectValue Root { get; }
        }

        internal readonly struct JsonObject
        {
            internal JsonObject(JsonObjectValue value) => Object = value;

            internal JsonObjectValue Object { get; }
        }
    }
}
