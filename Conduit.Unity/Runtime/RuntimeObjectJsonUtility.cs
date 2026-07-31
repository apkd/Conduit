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
        {
            var body = target switch
            {
                GameObject gameObject => SerializeGameObject(gameObject),
                Transform transform => SerializeTransform(transform),
                MonoBehaviour or ScriptableObject => JsonUtility.ToJson(target, true),
                _ => SerializeWritableProperties(target),
            };
            return Wrap(target.GetType().Name, body);
        }

        public static void FromJsonOverwrite(Object target, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("JSON payload was empty.");

            var body = UnwrapTarget(RuntimeJsonObject.Parse(json), target.GetType().Name);
            switch (target)
            {
                case GameObject gameObject:
                    OverwriteGameObject(gameObject, body);
                    return;
                case Transform transform:
                    OverwriteTransform(transform, body);
                    return;
                case MonoBehaviour:
                case ScriptableObject:
                    JsonUtility.FromJsonOverwrite(body.Source, target);
                    return;
                default:
                    OverwriteWritableProperties(target, body);
                    return;
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

        static void OverwriteGameObject(GameObject target, RuntimeJsonObject json)
        {
            foreach (var member in json.Members)
            {
                switch (member.Name)
                {
                    case "name":
                    case "m_Name":
                        target.name = ParseString(member);
                        break;
                    case "activeSelf":
                    case "m_IsActive":
                        target.SetActive(ParseBoolean(member));
                        break;
                    case "layer":
                    case "m_Layer":
                        target.layer = ParseInt32(member);
                        break;
                    case "tag":
                    case "m_TagString":
                        target.tag = ParseString(member);
                        break;
                    case "hideFlags":
                    case "m_ObjectHideFlags":
                        target.hideFlags = (HideFlags)ParseInt32(member);
                        break;
                }
            }
        }

        static void OverwriteTransform(Transform target, RuntimeJsonObject json)
        {
            foreach (var member in json.Members)
            {
                switch (member.Name)
                {
                    case "localPosition":
                    case "m_LocalPosition":
                        target.localPosition = ParseStruct<Vector3>(member);
                        break;
                    case "localRotation":
                    case "m_LocalRotation":
                        target.localRotation = ParseStruct<Quaternion>(member);
                        break;
                    case "localScale":
                    case "m_LocalScale":
                        target.localScale = ParseStruct<Vector3>(member);
                        break;
                }
            }
        }

        static void OverwriteWritableProperties(Object target, RuntimeJsonObject json)
        {
            var properties = GetWritableProperties(target.GetType())
                .ToDictionary(static property => property.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var member in json.Members)
            {
                if (!properties.TryGetValue(member.Name, out var property))
                    continue;

                try
                {
                    property.SetValue(target, ParseValue(member, property.PropertyType));
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
        }

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

        static object? ParseValue(RuntimeJsonMember member, Type type)
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

            return JsonUtility.FromJson(member.Source, type);
        }

        static T ParseStruct<T>(RuntimeJsonMember member) where T : struct =>
            (T)(JsonUtility.FromJson(member.Source, typeof(T))
                ?? throw new InvalidOperationException($"JSON property '{member.Name}' was invalid."));

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

        static RuntimeJsonObject UnwrapTarget(RuntimeJsonObject json, string typeName)
        {
            if (json.Members.Count != 1
                || !string.Equals(
                    json.Members[0].Name,
                    typeName,
                    StringComparison.OrdinalIgnoreCase
                ))
                return json;

            return RuntimeJsonObject.Parse(json.Members[0].Source);
        }

        static string Wrap(string typeName, string body)
        {
            var builder = new StringBuilder();
            builder.Append("{\n  ").Append(Quote(typeName)).Append(": ");
            AppendIndented(builder, body, 2);
            builder.Append("\n}");
            return builder.ToString();
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
