#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class SerializedJsonDiff
    {
        // sentinels retain JSON value kinds in the allocation-light flattened representation.
        internal const string EmptyObjectValue = "\u0001{}";
        internal const string EmptyArrayValue = "\u0002[]";
        internal const string StringValuePrefix = "\u0003";
        internal const string PrimitiveValuePrefix = "\u0004";
        const string NoSerializedPropertiesChangedMessage = "No serialized properties changed.";

        internal static void CollectChangedPaths(string beforeJson, string afterJson, List<string> changedPaths)
        {
            if (beforeJson == afterJson)
                return;

            using var pooledBeforeValues = ConduitPool.GetPooledDictionary<string, string>(out var beforeValues);
            using var pooledAfterValues = ConduitPool.GetPooledDictionary<string, string>(out var afterValues);
            if (!TryFlatten(beforeJson, beforeValues) || !TryFlatten(afterJson, afterValues))
                throw new InvalidOperationException("Could not diff serialized JSON after overwrite.");

            foreach (var pair in beforeValues)
            {
                if (!afterValues.Remove(pair.Key, out var afterValue) || pair.Value != afterValue)
                    changedPaths.Add(pair.Key);
            }

            foreach (var path in afterValues.Keys)
                changedPaths.Add(path);
        }

        internal static string FormatChangedPathList(List<string> changedPaths)
        {
            if (changedPaths.Count == 0)
                return NoSerializedPropertiesChangedMessage;

            changedPaths.Sort(StringComparer.Ordinal);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine("Applied changes:");
            foreach (var path in changedPaths)
            {
                builder.Append("- ");
                builder.Append(path);
                builder.Append('\n');
            }

            builder.Length--;
            return builder.ToString();
        }

        internal static string? GetComparableOwningGameObjectName(Object target, string json)
            => target is Component && TryReadRootNameOverwrite(json, out _) ? target.AsGameObject()?.name : null;

        internal static bool TryReadRootNameOverwrite(string json, out string name)
        {
            name = string.Empty;
            using var pooledValues = ConduitPool.GetPooledDictionary<string, string>(out var values);
            if (!TryFlatten(json, values))
                return false;

            if (values.TryGetValue("m_Name", out var directValue))
            {
                name = SerializedJsonValueDecoder.DecodeString("m_Name", directValue);
                return true;
            }

            foreach (var pair in values)
            {
                if (!pair.Key.EndsWith(".m_Name", StringComparison.Ordinal))
                    continue;

                var separatorIndex = pair.Key.IndexOf('.');
                if (separatorIndex < 0
                    || separatorIndex != pair.Key.LastIndexOf('.')
                    || separatorIndex != pair.Key.Length - "m_Name".Length - 1)
                    continue;

                name = SerializedJsonValueDecoder.DecodeString(pair.Key, pair.Value);
                return true;
            }

            return false;
        }

        internal static void AddOwningGameObjectNameChangeIfNeeded(Object updatedTarget, string? beforeOwningGameObjectName, List<string> changedPaths)
        {
            if (beforeOwningGameObjectName == null)
                return;

            var updatedOwningGameObjectName = updatedTarget.AsGameObject()?.name;
            if (updatedOwningGameObjectName == null
                || beforeOwningGameObjectName == updatedOwningGameObjectName
                || changedPaths.Contains("GameObject.m_Name"))
                return;

            changedPaths.Add("GameObject.m_Name");
        }

        internal static bool TryFlatten(string json, Dictionary<string, string> values)
        {
            var index = 0;
            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (!TryFlattenJsonValue(json, ref index, string.Empty, values))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            return index == json.Length;
        }

        static bool TryFlattenJsonValue(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (index >= json.Length)
                return false;

            return json[index] switch
            {
                '{' => TryFlattenJsonObject(json, ref index, path, values),
                '[' => TryFlattenJsonArray(json, ref index, path, values),
                '"' => TryReadJsonLeafString(json, ref index, path, values),
                _   => TryReadJsonPrimitive(json, ref index, path, values),
            };
        }

        static bool TryFlattenJsonObject(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (!JsonSyntaxReader.TryConsume(json, ref index, '{'))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (JsonSyntaxReader.TryConsume(json, ref index, '}'))
            {
                if (path.Length > 0)
                    values[path] = EmptyObjectValue;

                return true;
            }

            while (index < json.Length)
            {
                if (!JsonSyntaxReader.TryReadJsonString(json, ref index, out var propertyName))
                    return false;

                JsonSyntaxReader.SkipWhitespace(json, ref index);
                if (!JsonSyntaxReader.TryConsume(json, ref index, ':'))
                    return false;

                JsonSyntaxReader.SkipWhitespace(json, ref index);
                var childPath = path.Length == 0 ? propertyName : string.Concat(path, ".", propertyName);
                if (!TryFlattenJsonValue(json, ref index, childPath, values))
                    return false;

                JsonSyntaxReader.SkipWhitespace(json, ref index);
                if (JsonSyntaxReader.TryConsume(json, ref index, '}'))
                    return true;

                if (!JsonSyntaxReader.TryConsume(json, ref index, ','))
                    return false;

                JsonSyntaxReader.SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TryFlattenJsonArray(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (!JsonSyntaxReader.TryConsume(json, ref index, '['))
                return false;

            JsonSyntaxReader.SkipWhitespace(json, ref index);
            if (JsonSyntaxReader.TryConsume(json, ref index, ']'))
            {
                if (path.Length > 0)
                    values[path] = EmptyArrayValue;

                return true;
            }

            var elementIndex = 0;
            while (index < json.Length)
            {
                var childPath = string.Concat(path, "[", elementIndex.ToString(CultureInfo.InvariantCulture), "]");
                if (!TryFlattenJsonValue(json, ref index, childPath, values))
                    return false;

                elementIndex++;
                JsonSyntaxReader.SkipWhitespace(json, ref index);
                if (JsonSyntaxReader.TryConsume(json, ref index, ']'))
                    return true;

                if (!JsonSyntaxReader.TryConsume(json, ref index, ','))
                    return false;

                JsonSyntaxReader.SkipWhitespace(json, ref index);
            }

            return false;
        }

        static bool TryReadJsonLeafString(string json, ref int index, string path, Dictionary<string, string> values)
        {
            if (!JsonSyntaxReader.TryReadJsonString(json, ref index, out var value))
                return false;

            if (path.Length > 0)
                values[path] = StringValuePrefix + value;

            return true;
        }

        static bool TryReadJsonPrimitive(string json, ref int index, string path, Dictionary<string, string> values)
        {
            var start = index;
            while (index < json.Length && !char.IsWhiteSpace(json[index]) && json[index] is not ',' and not ']' and not '}')
                index++;

            if (index <= start)
                return false;

            if (path.Length > 0)
                values[path] = string.Create(
                    1 + index - start,
                    (Json: json, Start: start, Length: index - start),
                    static (destination, state) =>
                    {
                        destination[0] = PrimitiveValuePrefix[0];
                        state.Json.AsSpan(state.Start, state.Length).CopyTo(destination[1..]);
                    }
                );

            return true;
        }
    }
}
