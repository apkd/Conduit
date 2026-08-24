#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{

    static class ProjectSettingValueCodec
    {
        internal static string Format(object? value, Type declaredType)
        {
            if (value == null)
                return "null";

            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            if (type == typeof(string))
                return FormatString((string)value);
            if (type == typeof(char))
                return FormatString(value.ToString());
            if (type == typeof(bool))
                return (bool)value ? "true" : "false";
            if (type.IsEnum)
                return value.ToString();
            if (value is Object unityObject)
                return FormatObjectReference(unityObject);
            if (value is IFormattable formattable
                && (type.IsPrimitive || type == typeof(decimal) || type == typeof(Guid)))
                return formattable.ToString(null, CultureInfo.InvariantCulture);

            return JsonUtility.ToJson(value);
        }

        internal static object? Parse(string value, Type declaredType)
        {
            var nullableType = Nullable.GetUnderlyingType(declaredType);
            var type = nullableType ?? declaredType;
            if (value == "null")
            {
                if (!type.IsValueType || nullableType != null)
                    return null;

                throw new FormatException($"'{declaredType.Name}' cannot be cleared with null.");
            }

            if (type == typeof(string))
                return ParseString(value);
            if (type == typeof(char))
            {
                string parsed = ParseString(value);
                if (parsed.Length != 1)
                    throw new FormatException("A character setting requires exactly one character.");
                return parsed[0];
            }
            if (type == typeof(bool))
                return ParseBoolean(value);
            if (type.IsEnum)
                return Enum.Parse(type, value, ignoreCase: true);
            if (typeof(Object).IsAssignableFrom(type))
                return ParseObjectReference(value, type);

            try
            {
                if (type == typeof(Guid))
                    return Guid.Parse(value);
                if (type.IsPrimitive || type == typeof(decimal))
                    return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

                return JsonUtility.FromJson(value, type);
            }
            catch (Exception exception) when (exception is FormatException
                                              or OverflowException
                                              or ArgumentException
                                              or InvalidCastException)
            {
                throw new FormatException($"Could not parse '{value}' as {declaredType.Name}.", exception);
            }
        }

        // simple strings stay shell-friendly; JSON quoting preserves empty, whitespace, and literal "null" values.
        static string FormatString(string? value)
        {
            if (value == null)
                return "null";
            if (value.Length > 0
                && value != "null"
                && !char.IsWhiteSpace(value[0])
                && !char.IsWhiteSpace(value[^1])
                && value[0] != '"'
                && value[^1] != '"'
                && value.IndexOf('\r') < 0
                && value.IndexOf('\n') < 0
                && value.IndexOf('\t') < 0)
                return value;

            return ConduitSimpleJson.Quote(value);
        }

        static string ParseString(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
                return value;

            return ConduitSimpleJson.ParseValue(value) is ConduitSimpleJson.JsonStringValue text
                ? text.Value
                : throw new FormatException($"Could not parse '{value}' as String.");
        }

        static bool ParseBoolean(string value)
            => value.Trim().ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on"  => true,
                "false" or "0" or "no" or "off" => false,
                _ => throw new FormatException($"Could not parse '{value}' as Boolean."),
            };

        internal static string FormatObjectReference(Object value)
        {
            string path = AssetDatabase.GetAssetPath(value);
            if (path.Length > 0
                && AssetDatabase.LoadMainAssetAtPath(path) == value)
                return path;

            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out var guid, out var fileId))
                return $"{{\"guid\":\"{guid}\",\"file_id\":{fileId.ToString(CultureInfo.InvariantCulture)}}}";

            return $"{{\"entity_id\":\"{BridgeObjectId.Get(value).ToString(CultureInfo.InvariantCulture)}\"}}";
        }

        internal static Object? ParseObjectReference(string value, Type expectedType)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = (string)Parse(value, typeof(string))!;

            if (value.StartsWith("{", StringComparison.Ordinal))
            {
                if (ConduitSimpleJson.ParseValue(value) is not ConduitSimpleJson.JsonObjectValue reference)
                    throw new FormatException("An object reference requires a JSON object.");

                if (reference.Properties.TryGetValue("guid", out var guidValue))
                {
                    if (guidValue is not ConduitSimpleJson.JsonStringValue { Value: { Length: > 0 } } guid)
                        throw new FormatException("An object reference guid must be a non-empty string.");

                    string referencePath = AssetDatabase.GUIDToAssetPath(guid.Value);
                    if (!reference.Properties.TryGetValue("file_id", out var fileIdValue))
                    {
                        if (AssetDatabase.LoadAssetAtPath(referencePath, expectedType) is { } asset)
                            return asset;
                    }
                    else
                    {
                        if (fileIdValue is not ConduitSimpleJson.JsonNumberValue fileId)
                            throw new FormatException("An object reference file_id must be an integer.");

                        long expectedFileId = long.Parse(
                            fileId.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture
                        );
                        foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(referencePath))
                            if (expectedType.IsInstanceOfType(candidate)
                                && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                                    candidate,
                                    out _,
                                    out var candidateFileId
                                )
                                && candidateFileId == expectedFileId)
                                return candidate;
                    }

                    throw new FormatException(
                        $"Object reference '{value}' does not resolve to an asset assignable to {expectedType.Name}."
                    );
                }

                if (reference.Properties.TryGetValue("entity_id", out var entityIdValue))
                {
                    string? entityId = entityIdValue switch
                    {
                        ConduitSimpleJson.JsonStringValue text => text.Value,
                        ConduitSimpleJson.JsonNumberValue number => number.Value,
                        _ => null,
                    };
                    if (entityId != null
                        && BridgeObjectId.TryParse(entityId, out var objectId)
                        && ConduitObjectId.ResolveObjectId(objectId) is { } target
                        && expectedType.IsInstanceOfType(target))
                        return target;

                    throw new FormatException(
                        $"Object reference '{value}' does not resolve to a live {expectedType.Name}."
                    );
                }

                throw new FormatException("An object reference JSON object requires guid or entity_id.");
            }

            var result = AssetDatabase.LoadAssetAtPath(value, expectedType);
            if (result != null)
                return result;

            string path = AssetDatabase.GUIDToAssetPath(value);
            result = path.Length == 0 ? null : AssetDatabase.LoadAssetAtPath(path, expectedType);
            if (result == null)
                throw new FormatException(
                    $"'{value}' does not resolve to a project asset assignable to {expectedType.Name}."
                );
            return result;
        }
    }
}
