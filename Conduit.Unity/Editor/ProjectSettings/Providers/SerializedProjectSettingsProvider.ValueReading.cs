#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class SerializedProjectSettingsProvider
    {
        static string ReadProperty(SerializedProperty property)
        {
            if (IsGuid(property))
                return guidValueProperty!.GetValue(property)?.ToString() ?? string.Empty;

            return property.propertyType switch
            {
                SerializedPropertyType.Integer         => property.longValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.Boolean         => property.boolValue ? "true" : "false",
                SerializedPropertyType.Float => property.doubleValue.ToString(
                    "R",
                    CultureInfo.InvariantCulture
                ),
                SerializedPropertyType.String => ProjectSettingValueCodec.Format(
                    property.stringValue,
                    typeof(string)
                ),
                SerializedPropertyType.Color           => JsonUtility.ToJson(property.colorValue),
                SerializedPropertyType.ObjectReference => property.objectReferenceValue == null
                    ? "null"
                    : ProjectSettingValueCodec.FormatObjectReference(property.objectReferenceValue),
                SerializedPropertyType.ExposedReference => property.exposedReferenceValue == null
                    ? "null"
                    : ProjectSettingValueCodec.FormatObjectReference(property.exposedReferenceValue),
                SerializedPropertyType.LayerMask       => property.intValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.Enum            => property.enumValueIndex >= 0
                                                           && property.enumValueIndex < property.enumNames.Length
                    ? property.enumNames[property.enumValueIndex]
                    : property.intValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.Vector2         => JsonUtility.ToJson(property.vector2Value),
                SerializedPropertyType.Vector3         => JsonUtility.ToJson(property.vector3Value),
                SerializedPropertyType.Vector4         => JsonUtility.ToJson(property.vector4Value),
                SerializedPropertyType.Rect            => JsonUtility.ToJson(property.rectValue),
                SerializedPropertyType.Character => ProjectSettingValueCodec.Format(
                    (char)property.intValue,
                    typeof(char)
                ),
                SerializedPropertyType.AnimationCurve => JsonUtility.ToJson(
                    new AnimationCurveValue { value = property.animationCurveValue }
                ),
                SerializedPropertyType.Bounds          => JsonUtility.ToJson(property.boundsValue),
                SerializedPropertyType.Quaternion      => JsonUtility.ToJson(property.quaternionValue),
                SerializedPropertyType.Vector2Int      => JsonUtility.ToJson(property.vector2IntValue),
                SerializedPropertyType.Vector3Int      => JsonUtility.ToJson(property.vector3IntValue),
                SerializedPropertyType.RectInt         => JsonUtility.ToJson(property.rectIntValue),
                SerializedPropertyType.BoundsInt       => JsonUtility.ToJson(property.boundsIntValue),
                SerializedPropertyType.ArraySize       => property.intValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.Gradient => JsonUtility.ToJson(
                    new GradientValue { value = property.gradientValue }
                ),
                SerializedPropertyType.FixedBufferSize => property.fixedBufferSize.ToString(
                    CultureInfo.InvariantCulture
                ),
                SerializedPropertyType.Hash128         => property.hash128Value.ToString(),
                SerializedPropertyType.RenderingLayerMask => property.uintValue.ToString(
                    CultureInfo.InvariantCulture
                ),
                _ => throw new NotSupportedException(
                    $"Serialized setting '{property.propertyPath}' has unsupported type {property.propertyType}."
                ),
            };
        }

        static string ReadValue(SerializedProperty property)
            => IsSupportedLeaf(property)
                ? ReadProperty(property)
                : SerializeCompound(property);

        static string SerializeCompound(SerializedProperty property)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            AppendCompound(builder, property);
            return builder.ToString();
        }

        static void AppendCompound(StringBuilder builder, SerializedProperty property)
        {
            if (property.isArray)
            {
                builder.Append('[');
                for (int index = 0, count = property.arraySize; index < count; ++index)
                {
                    if (index > 0)
                        builder.Append(',');
                    AppendJson(builder, property.GetArrayElementAtIndex(index));
                }
                builder.Append(']');
                return;
            }

            builder.Append('{');
            var fieldCount = 0;
            foreach (var child in GetDirectChildren(property))
            {
                if (fieldCount++ > 0)
                    builder.Append(',');
                ConduitSimpleJson.AppendQuoted(builder, ToKey(child.name));
                builder.Append(':');
                AppendJson(builder, child);
            }
            builder.Append('}');
        }

        static void AppendJson(StringBuilder builder, SerializedProperty property)
        {
            if (!IsSupportedLeaf(property))
            {
                AppendCompound(builder, property);
                return;
            }
            if (IsGuid(property))
            {
                ConduitSimpleJson.AppendQuoted(builder, ReadProperty(property));
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    ConduitSimpleJson.AppendQuoted(builder, property.stringValue);
                    return;
                case SerializedPropertyType.Character:
                    ConduitSimpleJson.AppendQuoted(builder, ((char)property.intValue).ToString());
                    return;
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.Hash128:
                    ConduitSimpleJson.AppendQuoted(builder, ReadProperty(property));
                    return;
                case SerializedPropertyType.ObjectReference:
                    AppendReference(property.objectReferenceValue);
                    return;
                case SerializedPropertyType.ExposedReference:
                    AppendReference(property.exposedReferenceValue);
                    return;
                default:
                    builder.Append(ReadProperty(property));
                    return;
            }

            void AppendReference(Object? value)
            {
                if (value == null)
                {
                    builder.Append("null");
                    return;
                }

                string formatted = ProjectSettingValueCodec.FormatObjectReference(value);
                if (formatted.StartsWith("{", StringComparison.Ordinal))
                    builder.Append(formatted);
                else
                    ConduitSimpleJson.AppendQuoted(builder, formatted);
            }
        }

    }
}

