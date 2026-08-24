#nullable enable

using System;

namespace Conduit
{
    // built-in Unity JSON APIs cannot round-trip arbitrary editor settings
    // this DOM retains unknown values and ordering
    static partial class ConduitSimpleJson
    {
        internal static JsonDocument ParseObject(string json)
        {
            var parser = new Parser(json);
            if (parser.ParseValue() is not JsonObjectValue value)
                throw new InvalidOperationException("JSON root must be an object.");

            parser.ExpectEnd();
            return new(value);
        }

        internal static JsonValue? ParseValue(string json)
        {
            var parser = new Parser(json);
            var value = parser.ParseValue();
            parser.ExpectEnd();
            return value;
        }

        internal static bool ContainsComments(string json)
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

        internal static JsonObject EnsureObject(JsonObject parent, string propertyName)
        {
            if (parent.Object.Properties.TryGetValue(propertyName, out var value))
            {
                if (value is JsonObjectValue existingObject)
                    return new(existingObject);

                throw new InvalidOperationException($"JSON property '{propertyName}' must be an object.");
            }

            var objectValue = new JsonObjectValue();
            parent.Object.Properties[propertyName] = objectValue;
            return new(objectValue);
        }

        internal static JsonObject Root(JsonDocument document) => new(document.Root);

        internal static JsonObject? GetObject(JsonObject? parent, string propertyName)
        {
            if (parent is not { } jsonObject)
                return null;

            return jsonObject.Object.Properties.TryGetValue(propertyName, out var value)
                   && value is JsonObjectValue objectValue
                ? new JsonObject(objectValue)
                : null;
        }

        internal static void SetString(JsonObject target, string propertyName, string value)
            => target.Object.Properties[propertyName] = new JsonStringValue { Value = value };

        internal static void SetBool(JsonObject target, string propertyName, bool value)
            => target.Object.Properties[propertyName] = new JsonBoolValue { Value = value };

        internal static void SetStringArray(JsonObject target, string propertyName, params string[] values)
        {
            var array = new JsonArrayValue();
            foreach (string value in values)
                array.Items.Add(new JsonStringValue { Value = value });

            target.Object.Properties[propertyName] = array;
        }

        internal static void Remove(JsonObject target, string propertyName)
            => target.Object.Properties.Remove(propertyName);

        internal static string? GetString(JsonObject? target, string propertyName)
            => target is { } jsonObject
               && jsonObject.Object.Properties.TryGetValue(propertyName, out var value)
               && value is JsonStringValue stringValue
                ? stringValue.Value
                : null;

        internal static bool? GetBool(JsonObject? target, string propertyName)
            => target is { } jsonObject
               && jsonObject.Object.Properties.TryGetValue(propertyName, out var value)
               && value is JsonBoolValue boolValue
                ? boolValue.Value
                : null;

        internal static string? GetFirstString(JsonObject? target, string propertyName)
        {
            if (target is not { } jsonObject
                || !jsonObject.Object.Properties.TryGetValue(propertyName, out var value)
                || value is not JsonArrayValue { Items: { Count: > 0 } } array)
                return null;

            return array.Items[0] is JsonStringValue stringValue ? stringValue.Value : null;
        }
    }
}
