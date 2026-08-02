#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    /// <summary>Serializes and patches loaded Unity objects without editor-only serialization APIs.</summary>
    static class RuntimeObjectJsonUtility
    {
        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        public static string ToJson(Object target)
            => target switch
            {
                GameObject gameObject => SerializeGameObject(gameObject),
                Transform transform => SerializeTransform(transform),
                MonoBehaviour or ScriptableObject => JsonUtility.ToJson(target, true),
                _ => SerializeWritableProperties(target),
            };

        public static string FromJsonOverwrite(Object target, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("JSON payload was empty.");

            var body = UnwrapTarget(RuntimeJsonObject.Parse(json), target.GetType());
            var before = ToJson(target);
            var requestedPaths = new HashSet<string>(StringComparer.Ordinal);
            switch (target)
            {
                case GameObject gameObject:
                    OverwriteGameObject(gameObject, body, requestedPaths);
                    break;
                case Transform transform:
                    OverwriteTransform(transform, body, requestedPaths);
                    break;
                case MonoBehaviour:
                case ScriptableObject:
                    AddRequestedPaths(body, string.Empty, requestedPaths);
                    JsonUtility.FromJsonOverwrite(body.Source, target);
                    break;
                default:
                    OverwriteWritableProperties(target, body, requestedPaths);
                    break;
            }

            return FormatChanges(before, ToJson(target), requestedPaths);
        }

        static string FormatChanges(string before, string after, HashSet<string> requestedPaths)
        {
            var changed = new List<string>();
            CollectChangedPaths(RuntimeJsonObject.Parse(before), RuntimeJsonObject.Parse(after), string.Empty, changed);
            changed.RemoveAll(path => !requestedPaths.Any(requested =>
                string.Equals(path, requested, StringComparison.Ordinal)
                || path.StartsWith(requested + ".", StringComparison.Ordinal)
                || requested.StartsWith(path + ".", StringComparison.Ordinal)
            ));
            if (changed.Count == 0)
                return "No serialized properties changed.";

            changed.Sort(StringComparer.Ordinal);
            return "Applied changes:\n- " + string.Join("\n- ", changed);
        }

        static void CollectChangedPaths(
            RuntimeJsonObject before,
            RuntimeJsonObject after,
            string prefix,
            List<string> changed)
        {
            var beforeMembers = before.Members.ToDictionary(static member => member.Name, StringComparer.Ordinal);
            var afterMembers = after.Members.ToDictionary(static member => member.Name, StringComparer.Ordinal);
            foreach (var name in beforeMembers.Keys.Concat(afterMembers.Keys).Distinct(StringComparer.Ordinal))
            {
                var path = prefix.Length == 0 ? name : prefix + "." + name;
                if (!beforeMembers.TryGetValue(name, out var beforeMember)
                    || !afterMembers.TryGetValue(name, out var afterMember))
                {
                    changed.Add(path);
                    continue;
                }

                if (beforeMember.Source == afterMember.Source)
                    continue;
                if (beforeMember.Source.TrimStart().StartsWith("{", StringComparison.Ordinal)
                    && afterMember.Source.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    CollectChangedPaths(
                        RuntimeJsonObject.Parse(beforeMember.Source),
                        RuntimeJsonObject.Parse(afterMember.Source),
                        path,
                        changed
                    );
                else
                    changed.Add(path);
            }
        }

        static string SerializeGameObject(GameObject gameObject) =>
            WriteObject(
                ("name", Quote(gameObject.name)),
                ("activeSelf", gameObject.activeSelf ? "true" : "false"),
                ("layer", gameObject.layer.ToString(CultureInfo.InvariantCulture)),
                ("tag", Quote(gameObject.tag)),
                ("hideFlags", ((int)gameObject.hideFlags).ToString(CultureInfo.InvariantCulture))
            );

        static string SerializeTransform(Transform transform) =>
            WriteObject(
                ("localPosition", JsonUtility.ToJson(transform.localPosition, true)),
                ("localRotation", JsonUtility.ToJson(transform.localRotation, true)),
                ("localScale", JsonUtility.ToJson(transform.localScale, true))
            );

        static string SerializeWritableProperties(Object target)
        {
            var values = new List<(string Name, string Json)>();
            foreach (var property in GetWritableProperties(target.GetType()))
            {
                object? value;
                try
                {
                    value = property.GetValue(target);
                }
                catch
                {
                    continue;
                }

                if (TrySerializeValue(value, property.PropertyType, out var json))
                    values.Add((property.Name, json));
            }

            return WriteObject(values);
        }

        static void OverwriteGameObject(
            GameObject target,
            RuntimeJsonObject json,
            HashSet<string> requestedPaths)
        {
            var name = target.name;
            var activeSelf = target.activeSelf;
            var layer = target.layer;
            var tag = target.tag;
            var hideFlags = target.hideFlags;
            var setName = false;
            var setActiveSelf = false;
            var setLayer = false;
            var setTag = false;
            var setHideFlags = false;
            foreach (var member in json.Members)
            {
                switch (member.Name)
                {
                    case "name":
                    case "m_Name":
                        name = ParseString(member);
                        setName = true;
                        requestedPaths.Add("name");
                        break;
                    case "activeSelf":
                    case "m_IsActive":
                        activeSelf = ParseBoolean(member);
                        setActiveSelf = true;
                        requestedPaths.Add("activeSelf");
                        break;
                    case "layer":
                    case "m_Layer":
                        layer = ParseInt32(member);
                        if (layer is < 0 or > 31)
                            throw new InvalidOperationException("GameObject layer must be between 0 and 31.");
                        setLayer = true;
                        requestedPaths.Add("layer");
                        break;
                    case "tag":
                    case "m_TagString":
                        tag = ParseString(member);
                        target.CompareTag(tag); // validates the tag without mutating the object
                        setTag = true;
                        requestedPaths.Add("tag");
                        break;
                    case "hideFlags":
                    case "m_ObjectHideFlags":
                        hideFlags = (HideFlags)ParseInt32(member);
                        setHideFlags = true;
                        requestedPaths.Add("hideFlags");
                        break;
                    default:
                        throw UnknownProperty(target, member.Name);
                }
            }

            if (setName)
                target.name = name;
            if (setActiveSelf)
                target.SetActive(activeSelf);
            if (setLayer)
                target.layer = layer;
            if (setTag)
                target.tag = tag;
            if (setHideFlags)
                target.hideFlags = hideFlags;
        }

        static void OverwriteTransform(
            Transform target,
            RuntimeJsonObject json,
            HashSet<string> requestedPaths)
        {
            var localPosition = target.localPosition;
            var localRotation = target.localRotation;
            var localScale = target.localScale;
            var setLocalPosition = false;
            var setLocalRotation = false;
            var setLocalScale = false;
            foreach (var member in json.Members)
            {
                switch (member.Name)
                {
                    case "localPosition":
                    case "m_LocalPosition":
                        localPosition = ParseStruct(member, localPosition);
                        setLocalPosition = true;
                        AddRequestedPaths(member, "localPosition", requestedPaths);
                        break;
                    case "localRotation":
                    case "m_LocalRotation":
                        localRotation = ParseStruct(member, localRotation);
                        setLocalRotation = true;
                        AddRequestedPaths(member, "localRotation", requestedPaths);
                        break;
                    case "localScale":
                    case "m_LocalScale":
                        localScale = ParseStruct(member, localScale);
                        setLocalScale = true;
                        AddRequestedPaths(member, "localScale", requestedPaths);
                        break;
                    default:
                        throw UnknownProperty(target, member.Name);
                }
            }

            if (setLocalPosition)
                target.localPosition = localPosition;
            if (setLocalRotation)
                target.localRotation = localRotation;
            if (setLocalScale)
                target.localScale = localScale;
        }

        static void OverwriteWritableProperties(
            Object target,
            RuntimeJsonObject json,
            HashSet<string> requestedPaths)
        {
            var properties = GetWritableProperties(target.GetType())
                .ToDictionary(static property => property.Name, StringComparer.OrdinalIgnoreCase);
            var edits = new List<(PropertyInfo Property, object? Before, object? After)>();
            foreach (var member in json.Members)
            {
                if (!properties.TryGetValue(member.Name, out var property))
                    throw UnknownProperty(target, member.Name);

                try
                {
                    var before = property.GetValue(target);
                    edits.Add((property, before, ParseValue(member, property.PropertyType, before)));
                    AddRequestedPaths(member, property.Name, requestedPaths);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        $"Could not overwrite runtime property '{property.Name}' on '{target.GetType().Name}'.",
                        exception is TargetInvocationException { InnerException: { } inner }
                            ? inner
                            : exception
                    );
                }
            }

            var applied = 0;
            try
            {
                foreach (var edit in edits)
                {
                    edit.Property.SetValue(target, edit.After);
                    applied++;
                }
            }
            catch (Exception exception)
            {
                for (var index = applied - 1; index >= 0; index--)
                    edits[index].Property.SetValue(target, edits[index].Before);

                var property = edits[Math.Min(applied, edits.Count - 1)].Property;
                throw new InvalidOperationException(
                    $"Could not overwrite runtime property '{property.Name}' on '{target.GetType().Name}'.",
                    exception is TargetInvocationException { InnerException: { } inner }
                        ? inner
                        : exception
                );
            }
        }

        static InvalidOperationException UnknownProperty(Object target, string propertyName) =>
            new($"Runtime property '{propertyName}' does not exist or is not writable on '{target.GetType().Name}'.");

        static IEnumerable<PropertyInfo> GetWritableProperties(Type type) =>
            type.GetProperties(PublicInstance)
                .Where(static property =>
                    property.GetMethod?.IsPublic == true
                    && property.SetMethod?.IsPublic == true
                    && property.GetIndexParameters().Length == 0
                    && IsSupportedPropertyType(property.PropertyType)
                    && property.GetCustomAttribute<ObsoleteAttribute>() == null
                )
                .GroupBy(static property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .OrderBy(static property => property.Name, StringComparer.Ordinal);

        static bool IsSupportedPropertyType(Type type) =>
            type == typeof(string)
            || type == typeof(char)
            || type == typeof(decimal)
            || type.IsPrimitive
            || type.IsEnum
            || type.IsValueType && type != typeof(IntPtr) && type != typeof(UIntPtr);

        static bool TrySerializeValue(object? value, Type type, out string json)
        {
            switch (value)
            {
                case null:
                    json = "null";
                    return true;
                case string text:
                    json = Quote(text);
                    return true;
                case char character:
                    json = Quote(character.ToString());
                    return true;
                case bool boolean:
                    json = boolean ? "true" : "false";
                    return true;
            }

            if (type.IsEnum)
            {
                json = Convert.ToInt64(value, CultureInfo.InvariantCulture)
                    .ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (type.IsPrimitive || type == typeof(decimal))
            {
                if (value is float floatValue
                    && (float.IsNaN(floatValue) || float.IsInfinity(floatValue))
                    || value is double doubleValue
                    && (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)))
                {
                    json = "null";
                    return true;
                }

                json = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
                return true;
            }

            json = JsonUtility.ToJson(value, true);
            return json is not ("{}" or "");
        }

        static object? ParseValue(RuntimeJsonMember member, Type type, object? currentValue)
        {
            if (member.IsNull)
                return type.IsValueType ? Activator.CreateInstance(type) : null;

            if (type == typeof(string))
                return ParseString(member);
            if (type == typeof(char))
            {
                var text = ParseString(member);
                if (text.Length != 1)
                    throw new InvalidOperationException($"JSON property '{member.Name}' must contain one character.");
                return text[0];
            }

            if (type == typeof(bool))
                return ParseBoolean(member);
            if (type.IsEnum)
            {
                if (member.IsString)
                    return Enum.Parse(type, ParseString(member), true);

                var number = Convert.ChangeType(
                    member.Source,
                    Enum.GetUnderlyingType(type),
                    CultureInfo.InvariantCulture
                );
                return Enum.ToObject(type, number!);
            }

            if (type.IsPrimitive || type == typeof(decimal))
                return Convert.ChangeType(member.Source, type, CultureInfo.InvariantCulture);

            var source = member.Source;
            if (currentValue != null
                && source.TrimStart().StartsWith("{", StringComparison.Ordinal)
                && TrySerializeValue(currentValue, type, out var currentJson)
                && currentJson.TrimStart().StartsWith("{", StringComparison.Ordinal))
                source = MergeObjects(currentJson, source);

            return JsonUtility.FromJson(source, type);
        }

        static T ParseStruct<T>(RuntimeJsonMember member, T currentValue) where T : struct =>
            (T)(ParseValue(member, typeof(T), currentValue)
                ?? throw new InvalidOperationException($"JSON property '{member.Name}' was invalid."));

        static string MergeObjects(string currentJson, string patchJson)
        {
            var current = RuntimeJsonObject.Parse(currentJson);
            var patch = RuntimeJsonObject.Parse(patchJson);
            var values = current.Members
                .Select(static member => (member.Name, Json: member.Source))
                .ToList();
            var indexes = values
                .Select(static (value, index) => (value.Name, Index: index))
                .ToDictionary(static value => value.Name, static value => value.Index, StringComparer.Ordinal);

            foreach (var member in patch.Members)
            {
                var source = member.Source;
                if (indexes.TryGetValue(member.Name, out var index))
                {
                    var existing = values[index].Json;
                    if (existing.TrimStart().StartsWith("{", StringComparison.Ordinal)
                        && source.TrimStart().StartsWith("{", StringComparison.Ordinal))
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
            if (member.Source.TrimStart().StartsWith("{", StringComparison.Ordinal))
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
                || !json.Members[0].Source.TrimStart().StartsWith("{", StringComparison.Ordinal))
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

            var builder = new StringBuilder("{\n");
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                    builder.Append(",\n");

                builder.Append("  ")
                    .Append(Quote(values[index].Name))
                    .Append(": ");
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
            var builder = new StringBuilder(value.Length + 2);
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

            return builder.Append('"').ToString();
        }
    }

    readonly struct RuntimeJsonMember
    {
        public RuntimeJsonMember(string name, string source)
        {
            Name = name;
            Source = source;
        }

        public string Name { get; }
        public string Source { get; }
        public bool IsNull => string.Equals(Source, "null", StringComparison.Ordinal);
        public bool IsString => Source.Length >= 2 && Source[0] == '"';
    }

    sealed class RuntimeJsonObject
    {
        RuntimeJsonObject(string source, IReadOnlyList<RuntimeJsonMember> members)
        {
            Source = source;
            Members = members;
        }

        public string Source { get; }
        public IReadOnlyList<RuntimeJsonMember> Members { get; }

        public static RuntimeJsonObject Parse(string source)
        {
            var parser = new Parser(source);
            var members = parser.ParseObject();
            parser.ExpectEnd();
            return new(source, members);
        }

        public static string ParseString(string source)
        {
            var parser = new Parser(source);
            var value = parser.ReadString();
            parser.ExpectEnd();
            return value;
        }

        sealed class Parser
        {
            readonly string source;
            int index;

            public Parser(string source) => this.source = source;

            public IReadOnlyList<RuntimeJsonMember> ParseObject()
            {
                SkipWhitespace();
                Expect('{');
                var members = new List<RuntimeJsonMember>();
                SkipWhitespace();
                if (TryConsume('}'))
                    return members;

                while (true)
                {
                    SkipWhitespace();
                    var name = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    var valueStart = index;
                    SkipValue();
                    members.Add(new(name, source.Substring(valueStart, index - valueStart)));
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return members;
                    Expect(',');
                }
            }

            public string ReadString()
            {
                SkipWhitespace();
                Expect('"');
                var builder = new StringBuilder();
                while (index < source.Length)
                {
                    var character = source[index++];
                    if (character == '"')
                        return builder.ToString();
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (index >= source.Length)
                        throw InvalidJson();
                    switch (source[index++])
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
                            if (index + 4 > source.Length
                                || !ushort.TryParse(
                                    source.Substring(index, 4),
                                    NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture,
                                    out var codeUnit
                                ))
                                throw InvalidJson();

                            builder.Append((char)codeUnit);
                            index += 4;
                            break;
                        default:
                            throw InvalidJson();
                    }
                }

                throw InvalidJson();
            }

            public void ExpectEnd()
            {
                SkipWhitespace();
                if (index != source.Length)
                    throw InvalidJson();
            }

            void SkipValue()
            {
                SkipWhitespace();
                if (index >= source.Length)
                    throw InvalidJson();

                switch (source[index])
                {
                    case '"':
                        ReadString();
                        return;
                    case '{':
                        SkipObject();
                        return;
                    case '[':
                        SkipArray();
                        return;
                    default:
                        var start = index;
                        while (index < source.Length
                               && !char.IsWhiteSpace(source[index])
                               && source[index] is not (',' or '}' or ']'))
                            index++;
                        if (index == start)
                            throw InvalidJson();

                        var token = source.Substring(start, index - start);
                        if (token is not ("true" or "false" or "null")
                            && !double.TryParse(
                                token,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out _
                            ))
                            throw InvalidJson();
                        return;
                }
            }

            void SkipObject()
            {
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                    return;

                while (true)
                {
                    ReadString();
                    SkipWhitespace();
                    Expect(':');
                    SkipValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return;
                    Expect(',');
                }
            }

            void SkipArray()
            {
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                    return;

                while (true)
                {
                    SkipValue();
                    SkipWhitespace();
                    if (TryConsume(']'))
                        return;
                    Expect(',');
                }
            }

            void SkipWhitespace()
            {
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                    index++;
            }

            bool TryConsume(char expected)
            {
                if (index >= source.Length || source[index] != expected)
                    return false;

                index++;
                return true;
            }

            void Expect(char expected)
            {
                if (!TryConsume(expected))
                    throw InvalidJson();
            }

            static InvalidOperationException InvalidJson() =>
                new("JSON payload was invalid.");
        }
    }
}
