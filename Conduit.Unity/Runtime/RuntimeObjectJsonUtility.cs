#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    /// <summary>Serializes and patches loaded Unity objects without editor-only serialization APIs.</summary>
    static class RuntimeObjectJsonUtility
    {
        const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        static readonly ConcurrentDictionary<Type, PropertyInfo[]> writableProperties = new();
        static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> writablePropertyLookups = new();

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
            using var pooledRequestedPaths = CollectionPool<HashSet<string>, string>.Get(
                out var requestedPaths
            );
            requestedPaths.Clear();
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
            using var pooledChanged = ListPool<string>.Get(out var changed);
            changed.Clear();
            CollectChangedPaths(RuntimeJsonObject.Parse(before), RuntimeJsonObject.Parse(after), string.Empty, changed);
            var retainedCount = 0;
            for (var index = 0; index < changed.Count; ++index)
            {
                var path = changed[index];
                if (IsRequestedPath(path))
                    changed[retainedCount++] = path;
            }
            if (retainedCount < changed.Count)
                changed.RemoveRange(retainedCount, changed.Count - retainedCount);
            if (changed.Count == 0)
                return "No serialized properties changed.";

            changed.Sort(StringComparer.Ordinal);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append("Applied changes:");
            foreach (var path in changed)
                builder.Append("\n- ").Append(path);

            return builder.ToString();

            bool IsRequestedPath(string path)
            {
                if (requestedPaths.Contains(path))
                    return true;

                foreach (var requested in requestedPaths)
                    if (IsNestedPath(path, requested) || IsNestedPath(requested, path))
                        return true;

                return false;
            }
        }

        static bool IsNestedPath(string path, string prefix)
            => path.Length > prefix.Length
               && path.StartsWith(prefix, StringComparison.Ordinal)
               && path[prefix.Length] == '.';

        static bool IsJsonObject(string json)
        {
            foreach (var character in json)
            {
                if (char.IsWhiteSpace(character))
                    continue;

                return character == '{';
            }

            return false;
        }

        static void CollectChangedPaths(
            RuntimeJsonObject before,
            RuntimeJsonObject after,
            string prefix,
            List<string> changed)
        {
            using var pooledAfterMembers = DictionaryPool<string, RuntimeJsonMember>.Get(
                out var afterMembers
            );
            afterMembers.Clear();
            afterMembers.EnsureCapacity(after.Members.Count);
            foreach (var member in after.Members)
                afterMembers.Add(member.Name, member);

            foreach (var beforeMember in before.Members)
            {
                var name = beforeMember.Name;
                var path = prefix.Length == 0 ? name : prefix + "." + name;
                if (!afterMembers.Remove(name, out var afterMember))
                {
                    changed.Add(path);
                    continue;
                }

                if (beforeMember.Source == afterMember.Source)
                    continue;
                if (beforeMember.IsObject && afterMember.IsObject)
                    CollectChangedPaths(
                        RuntimeJsonObject.Parse(beforeMember.Source),
                        RuntimeJsonObject.Parse(afterMember.Source),
                        path,
                        changed
                    );
                else
                    changed.Add(path);
            }

            foreach (var name in afterMembers.Keys)
                changed.Add(prefix.Length == 0 ? name : prefix + "." + name);
        }

        static string SerializeGameObject(GameObject gameObject)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                gameObject.name.Length + gameObject.tag.Length + 128
            );
            builder.Append("{\n  \"name\": ");
            AppendQuoted(builder, gameObject.name);
            builder.Append(",\n  \"activeSelf\": ")
                .Append(gameObject.activeSelf ? "true" : "false")
                .Append(",\n  \"layer\": ")
                .Append(gameObject.layer.ToString(CultureInfo.InvariantCulture))
                .Append(",\n  \"tag\": ");
            AppendQuoted(builder, gameObject.tag);
            return builder
                .Append(",\n  \"hideFlags\": ")
                .Append(((int)gameObject.hideFlags).ToString(CultureInfo.InvariantCulture))
                .Append("\n}")
                .ToString();
        }

        static string SerializeTransform(Transform transform)
        {
            var localPosition = JsonUtility.ToJson(transform.localPosition, true);
            var localRotation = JsonUtility.ToJson(transform.localRotation, true);
            var localScale = JsonUtility.ToJson(transform.localScale, true);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                localPosition.Length + localRotation.Length + localScale.Length + 96
            );
            builder.Append("{\n  \"localPosition\": ");
            AppendIndented(builder, localPosition, 2);
            builder.Append(",\n  \"localRotation\": ");
            AppendIndented(builder, localRotation, 2);
            builder.Append(",\n  \"localScale\": ");
            AppendIndented(builder, localScale, 2);
            return builder.Append("\n}").ToString();
        }

        static string SerializeWritableProperties(Object target)
        {
            var properties = GetWritableProperties(target.GetType());
            using var pooledBuilder = BridgeStringBuilderPool.Rent(
                out var builder,
                4 + properties.Length * 32
            );
            var count = 0;
            foreach (var property in properties)
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

                if (!TrySerializeValue(value, property.PropertyType, out var json))
                    continue;

                builder.Append(count++ == 0 ? "{\n  " : ",\n  ");
                AppendQuoted(builder, property.Name);
                builder.Append(": ");
                AppendIndented(builder, json, 2);
            }

            return count == 0 ? "{}" : builder.Append("\n}").ToString();
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
            var properties = writablePropertyLookups.GetOrAdd(
                target.GetType(),
                static type =>
                {
                    var writable = GetWritableProperties(type);
                    var lookup = new Dictionary<string, PropertyInfo>(
                        writable.Length,
                        StringComparer.OrdinalIgnoreCase
                    );
                    foreach (var property in writable)
                        lookup.Add(property.Name, property);
                    return lookup;
                }
            );
            using var pooledEdits = ListPool<(
                PropertyInfo Property,
                object? Before,
                object? After
            )>.Get(out var edits);
            edits.Clear();
            if (edits.Capacity < json.Members.Count)
                edits.Capacity = json.Members.Count;
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

        static PropertyInfo[] GetWritableProperties(Type type)
            => writableProperties.GetOrAdd(type, static value =>
            {
                var candidates = value.GetProperties(PublicInstance);
                var firstByName = new Dictionary<string, PropertyInfo>(
                    candidates.Length,
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (var property in candidates)
                    if (property.GetMethod?.IsPublic == true
                        && property.SetMethod?.IsPublic == true
                        && property.GetIndexParameters().Length == 0
                        && IsSupportedPropertyType(property.PropertyType)
                        && !property.IsDefined(typeof(ObsoleteAttribute), inherit: false))
                        firstByName.TryAdd(property.Name, property);

                var properties = new PropertyInfo[firstByName.Count];
                firstByName.Values.CopyTo(properties, 0);
                Array.Sort(properties, static (left, right) => string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.Ordinal
                ));
                return properties;
            });

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
                && member.IsObject
                && TrySerializeValue(currentValue, type, out var currentJson)
                && IsJsonObject(currentJson))
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
        public bool IsObject => Source.Length > 0 && Source[0] == '{';
    }

    readonly struct RuntimeJsonObject
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

        struct Parser
        {
            readonly string source;
            int index;

            public Parser(string source)
            {
                this.source = source;
                index = 0;
            }

            public IReadOnlyList<RuntimeJsonMember> ParseObject()
            {
                SkipWhitespace();
                Expect('{');
                var members = new List<RuntimeJsonMember>(8);
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

            public string ReadString() => ReadString(materialize: true)!;

            string? ReadString(bool materialize)
            {
                SkipWhitespace();
                Expect('"');
                var segmentStart = index;
                StringBuilder? builder = null;
                BridgeStringBuilderPool.StringBuilderHandle pooledBuilder = default;
                try
                {
                    while (index < source.Length)
                    {
                        var character = source[index++];
                        if (character == '"')
                        {
                            if (!materialize)
                                return null;
                            if (builder == null)
                                return source.Substring(segmentStart, index - segmentStart - 1);

                            builder.Append(source, segmentStart, index - segmentStart - 1);
                            return builder.ToString();
                        }
                        if (character != '\\')
                            continue;

                        if (index >= source.Length)
                            throw InvalidJson();
                        if (materialize && builder == null)
                        {
                            pooledBuilder = BridgeStringBuilderPool.Rent(
                                out var rentedBuilder
                            );
                            builder = rentedBuilder;
                        }
                        if (materialize)
                            builder!.Append(source, segmentStart, index - segmentStart - 1);
                        switch (source[index++])
                        {
                            case '"':
                                if (materialize)
                                    builder!.Append('"');
                                break;
                            case '\\':
                                if (materialize)
                                    builder!.Append('\\');
                                break;
                            case '/':
                                if (materialize)
                                    builder!.Append('/');
                                break;
                            case 'b':
                                if (materialize)
                                    builder!.Append('\b');
                                break;
                            case 'f':
                                if (materialize)
                                    builder!.Append('\f');
                                break;
                            case 'n':
                                if (materialize)
                                    builder!.Append('\n');
                                break;
                            case 'r':
                                if (materialize)
                                    builder!.Append('\r');
                                break;
                            case 't':
                                if (materialize)
                                    builder!.Append('\t');
                                break;
                            case 'u':
                                if (index + 4 > source.Length
                                    || !ushort.TryParse(
                                        source.AsSpan(index, 4),
                                        NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture,
                                        out var codeUnit
                                    ))
                                    throw InvalidJson();

                                if (materialize)
                                    builder!.Append((char)codeUnit);
                                index += 4;
                                break;
                            default:
                                throw InvalidJson();
                        }

                        segmentStart = index;
                    }
                }
                finally
                {
                    pooledBuilder.Dispose();
                }

                throw InvalidJson();
            }

            void SkipString() => ReadString(materialize: false);

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
                        SkipString();
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

                        var token = source.AsSpan(start, index - start);
                        if (!token.Equals("true".AsSpan(), StringComparison.Ordinal)
                            && !token.Equals("false".AsSpan(), StringComparison.Ordinal)
                            && !token.Equals("null".AsSpan(), StringComparison.Ordinal)
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
                    SkipString();
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
