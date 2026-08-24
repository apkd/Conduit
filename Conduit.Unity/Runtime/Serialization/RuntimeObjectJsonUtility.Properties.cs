#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    static partial class RuntimeObjectJsonUtility
    {
        static InvalidOperationException UnknownProperty(Object target, string propertyName) =>
            new($"Runtime property '{propertyName}' does not exist or is not writable on '{target.GetType().Name}'.");

        static WritablePropertySet GetWritablePropertySet(Type type)
            => writablePropertySets.GetOrAdd(type, static value =>
            {
                var candidates = value.GetProperties(PublicInstance);
                var byName = new Dictionary<string, PropertyInfo>(
                    candidates.Length,
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (var property in candidates)
                    if (property.GetMethod?.IsPublic == true
                        && property.SetMethod?.IsPublic == true
                        && property.GetIndexParameters().Length == 0
                        && IsSupportedPropertyType(property.PropertyType)
                        && !property.IsDefined(typeof(ObsoleteAttribute), inherit: false))
                        byName.TryAdd(property.Name, property);

                var properties = new PropertyInfo[byName.Count];
                byName.Values.CopyTo(properties, 0);
                Array.Sort(properties, static (left, right) => string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.Ordinal
                ));
                return new(properties, byName);
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
        readonly struct WritablePropertySet
        {
            internal WritablePropertySet(
                PropertyInfo[] ordered,
                Dictionary<string, PropertyInfo> byName)
            {
                Ordered = ordered;
                ByName = byName;
            }

            internal PropertyInfo[] Ordered { get; }
            internal Dictionary<string, PropertyInfo> ByName { get; }
        }
    }
}
