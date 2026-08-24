#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine.Pool;

namespace Conduit.Runtime
{
    static partial class RuntimeObjectJsonUtility
    {
        static string MergeObjects(string currentJson, string patchJson)
        {
            var current = RuntimeJsonObject.Parse(currentJson);
            var patch = RuntimeJsonObject.Parse(patchJson);
            using var pooledValues = ListPool<(string Name, string Json)>.Get(out var values);
            values.Clear();
            var capacity = current.Members.Count + patch.Members.Count;
            if (values.Capacity < capacity)
                values.Capacity = capacity;
            using var pooledIndexes = DictionaryPool<string, int>.Get(out var indexes);
            indexes.Clear();
            indexes.EnsureCapacity(capacity);
            foreach (var member in current.Members)
            {
                indexes.Add(member.Name, values.Count);
                values.Add((member.Name, member.Source));
            }

            foreach (var member in patch.Members)
            {
                var source = member.Source;
                if (indexes.TryGetValue(member.Name, out var index))
                {
                    var existing = values[index].Json;
                    if (IsJsonObject(existing) && IsJsonObject(source))
                        source = MergeObjects(existing, source);

                    values[index] = (member.Name, source);
                }
                else
                {
                    indexes.Add(member.Name, values.Count);
                    values.Add((member.Name, source));
                }
            }

            return WriteObject(values);
        }

        static void AddRequestedPaths(
            RuntimeJsonObject json,
            string prefix,
            HashSet<string> requestedPaths)
        {
            foreach (var member in json.Members)
                AddRequestedPaths(member, prefix.Length == 0 ? member.Name : prefix + "." + member.Name, requestedPaths);
        }

        static void AddRequestedPaths(
            RuntimeJsonMember member,
            string path,
            HashSet<string> requestedPaths)
        {
            if (member.IsObject)
            {
                var nested = RuntimeJsonObject.Parse(member.Source);
                if (nested.Members.Count > 0)
                {
                    AddRequestedPaths(nested, path, requestedPaths);
                    return;
                }
            }

            requestedPaths.Add(path);
        }

        static string ParseString(RuntimeJsonMember member) =>
            RuntimeJsonObject.ParseString(member.Source);

        static bool ParseBoolean(RuntimeJsonMember member)
        {
            if (!bool.TryParse(member.Source, out var value))
                throw new InvalidOperationException($"JSON property '{member.Name}' must be a boolean.");
            return value;
        }

        static int ParseInt32(RuntimeJsonMember member)
        {
            if (!int.TryParse(
                    member.Source,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value
                ))
                throw new InvalidOperationException($"JSON property '{member.Name}' must be a 32-bit integer.");
            return value;
        }

        static RuntimeJsonObject UnwrapTarget(RuntimeJsonObject json, Type targetType)
        {
            if (json.Members.Count != 1
                || json.Members[0].Name.Length == 0
                || !char.IsUpper(json.Members[0].Name[0])
                || !json.Members[0].IsObject)
                return json;

            var wrapperName = json.Members[0].Name;
            var matches = false;
            for (var current = targetType; current != null && current != typeof(object); current = current.BaseType)
                matches |= string.Equals(wrapperName, current.Name, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                throw new InvalidOperationException(
                    $"JSON wrapper '{wrapperName}' does not match target type '{targetType.Name}'."
                );

            return RuntimeJsonObject.Parse(json.Members[0].Source);
        }

        static string WriteObject(params (string Name, string Json)[] values) =>
            WriteObject((IReadOnlyList<(string Name, string Json)>)values);

        static string WriteObject(IReadOnlyList<(string Name, string Json)> values)
        {
            if (values.Count == 0)
                return "{}";

            var capacity = 4;
            for (var index = 0; index < values.Count; index++)
                capacity += values[index].Name.Length + values[index].Json.Length + 8;
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder, capacity);
            builder.Append("{\n");
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                    builder.Append(",\n");

                builder.Append("  ");
                AppendQuoted(builder, values[index].Name);
                builder.Append(": ");
                AppendIndented(builder, values[index].Json, 2);
            }

            return builder.Append("\n}").ToString();
        }

        static void AppendIndented(StringBuilder builder, string value, int spaces)
        {
            foreach (var character in value)
            {
                builder.Append(character);
                if (character == '\n')
                    builder.Append(' ', spaces);
            }
        }

        static string Quote(string value)
        {
            var requiresEscaping = false;
            foreach (var character in value)
                if (character is '\\' or '"' || char.IsControl(character))
                {
                    requiresEscaping = true;
                    break;
                }

            if (!requiresEscaping)
                return string.Concat("\"", value, "\"");

            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                value.Length + 2
            );
            AppendQuoted(builder, value);
            return builder.ToString();
        }

        static void AppendQuoted(StringBuilder builder, string value)
        {
            builder.Append('"');
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
                        if (char.IsControl(character))
                            builder.Append("\\u")
                                .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(character);
                        break;
                }
            }

            builder.Append('"');
        }
    }
}
