#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ShowTool
    {
        static void AppendAssetObject(
            StringBuilder builder,
            Object assetObject,
            string label,
            string assetPath)
        {
            builder.Append(label)
                .Append(": ")
                .AppendLine(DescribeObject(assetObject, assetPath));
            AppendObjectIdentifiers(builder, assetObject, 2, includeGuid: true, assetPath: assetPath);
            AppendSerializableFields(builder, assetObject, 2);
            AppendNonSerializableFields(builder, assetObject, 2);
            builder.AppendLine();
        }

        static void AppendSerializableFields(StringBuilder builder, Object target, int indent)
        {
            try
            {
                using var serializedObject = new SerializedObject(target);
                var iterator = serializedObject.GetIterator();
                var enterChildren = true;
                var hasAny = false;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.depth != 0 || iterator.propertyPath == "m_ObjectHideFlags")
                        continue;

                    if (!hasAny)
                    {
                        builder.Append(' ', indent);
                        builder.AppendLine("Serializable:");
                        hasAny = true;
                    }

                    AppendSerializedProperty(builder, target, iterator, indent + 2);
                }
            }
            catch (Exception exception)
            {
                builder.Append(' ', indent);
                builder.AppendLine("Serializable:");
                builder.Append(' ', indent + 2);
                builder.Append("- <unavailable: ")
                    .Append(exception.Message)
                    .AppendLine(">");
            }
        }

        static void AppendNonSerializableFields(StringBuilder builder, object target, int indent)
        {
            var fields = GetInspectableFields(target.GetType());
            var hasAny = false;
            foreach (var field in fields)
            {
                if (IsUnitySerializableField(field))
                    continue;

                if (!hasAny)
                {
                    builder.Append(' ', indent);
                    builder.AppendLine("Non-Serializable:");
                    hasAny = true;
                }

                TryFormatFieldValue(field, target, 0, out var valueText);

                builder.Append(' ', indent + 2);
                builder.Append("- ")
                    .Append(field.Name)
                    .Append(": ")
                    .AppendLine(valueText);
            }
        }

        static void AppendSerializedProperty(StringBuilder builder, Object target, SerializedProperty property, int indent)
        {
            if (property is { isArray: true, propertyType: not SerializedPropertyType.String })
            {
                builder.Append(' ', indent);
                builder.Append("- ")
                    .Append(property.name)
                    .Append(": ")
                    .AppendLine(FormatArrayProperty(target, property));
                return;
            }

            if (property is { hasVisibleChildren: true, propertyType: SerializedPropertyType.Generic })
            {
                builder.Append(' ', indent);
                builder.Append("- ").Append(property.name).AppendLine(":");
                AppendImmediateChildren(builder, target, property, indent + 2);

                return;
            }

            builder.Append(' ', indent);
            builder.Append("- ")
                .Append(property.name)
                .Append(": ")
                .AppendLine(FormatSerializedValue(property));
        }

        static void AppendImmediateChildren(
            StringBuilder builder,
            Object target,
            SerializedProperty property,
            int indent)
        {
            var cursor = property.Copy();
            var end = cursor.GetEndProperty();
            var enterChildren = true;
            while (cursor.NextVisible(enterChildren) && !SerializedProperty.EqualContents(cursor, end))
            {
                enterChildren = false;
                if (cursor.depth == property.depth + 1)
                    AppendSerializedProperty(builder, target, cursor, indent);
            }
        }

        static string FormatArrayProperty(Object target, SerializedProperty property)
        {
            var count = property.arraySize;
            var elementType = GetEnumerableElementType(ResolveDeclaredType(target.GetType(), property.propertyPath)) ?? typeof(object);
            if (count == 0)
                return elementType == typeof(bool) ? string.Empty : "[]";

            var previewCount = GetPreviewCount(elementType);
            if (elementType == typeof(bool))
            {
                using var pooledBits = ConduitPool.GetStringBuilder(out var bits);
                var visibleCount = count <= previewCount ? count : previewCount - 1;
                for (var index = 0; index < visibleCount; ++index)
                    bits.Append(FormatBit(index));

                if (count <= previewCount)
                    return bits.ToString();

                bits.Append("...");
                bits.Append(FormatBit(count - 1));
                bits.Append(" (n=").Append(count).Append(')');
                return bits.ToString();

                char FormatBit(int index)
                    => FormatSerializedElement(target, property.GetArrayElementAtIndex(index), 1) == "true" ? '1' : '0';
            }

            using var pooledPreview = ConduitPool.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            var visibleItems = count <= previewCount ? count : previewCount - 1;
            for (var index = 0; index < visibleItems; ++index)
                AppendPreviewItem(
                    preview,
                    ref appendedCount,
                    FormatSerializedElement(target, property.GetArrayElementAtIndex(index), 1)
                );

            if (count <= previewCount)
                return preview.Append(']').ToString();

            AppendPreviewItem(preview, ref appendedCount, "...");
            AppendPreviewItem(
                preview,
                ref appendedCount,
                FormatSerializedElement(target, property.GetArrayElementAtIndex(count - 1), 1)
            );
            return preview.Append("] (n=").Append(count).Append(')').ToString();
        }

        static string FormatSerializedElement(Object target, SerializedProperty property, int depth)
        {
            if (depth > 1)
                return $"<{property.propertyType}>";

            if (property is { isArray: true, propertyType: not SerializedPropertyType.String })
                return FormatArrayProperty(target, property);

            if (property is { hasVisibleChildren: true, propertyType: SerializedPropertyType.Generic })
            {
                using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
                builder.Append('{');
                var cursor = property.Copy();
                var end = cursor.GetEndProperty();
                var enterChildren = true;
                var childCount = 0;
                while (cursor.NextVisible(enterChildren)
                       && !SerializedProperty.EqualContents(cursor, end))
                {
                    enterChildren = false;
                    if (cursor.depth != property.depth + 1)
                        continue;

                    if (childCount < MaxCollectionPreview)
                    {
                        if (childCount > 0)
                            builder.Append(", ");

                        builder.Append(cursor.name);
                        builder.Append('=');
                        builder.Append(FormatSerializedElement(target, cursor, depth + 1));
                    }
                    childCount++;
                }

                if (childCount == 0)
                    return "{}";
                if (childCount > MaxCollectionPreview)
                    builder.Append(", ...");

                builder.Append('}');
                return builder.ToString();
            }

            return FormatSerializedValue(property);
        }

        static string FormatSerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return property.floatValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return FormatString(property.stringValue);
                case SerializedPropertyType.Color:
                    return $"rgba({property.colorValue.r:0.###}, {property.colorValue.g:0.###}, {property.colorValue.b:0.###}, {property.colorValue.a:0.###})";
                case SerializedPropertyType.ObjectReference:
                    return DescribeObject(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return FormatVector(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return FormatVector(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return FormatVector(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Rect:
                    return $"Rect(x={property.rectValue.x:0.###}, y={property.rectValue.y:0.###}, w={property.rectValue.width:0.###}, h={property.rectValue.height:0.###})";
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return FormatString(char.ConvertFromUtf32(property.intValue));
                case SerializedPropertyType.AnimationCurve:
                    return $"AnimationCurve(keys={property.animationCurveValue?.length ?? 0})";
                case SerializedPropertyType.Bounds:
                    return $"Bounds(center={FormatVector(property.boundsValue.center.x, property.boundsValue.center.y, property.boundsValue.center.z)}, size={FormatVector(property.boundsValue.size.x, property.boundsValue.size.y, property.boundsValue.size.z)})";
                case SerializedPropertyType.Gradient:
                    return "Gradient(...)";
                case SerializedPropertyType.Quaternion:
                    return FormatVector(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                case SerializedPropertyType.ExposedReference:
                    return DescribeObject(property.exposedReferenceValue);
                case SerializedPropertyType.FixedBufferSize:
                    return property.fixedBufferSize.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2Int:
                    return $"({property.vector2IntValue.x}, {property.vector2IntValue.y})";
                case SerializedPropertyType.Vector3Int:
                    return $"({property.vector3IntValue.x}, {property.vector3IntValue.y}, {property.vector3IntValue.z})";
                case SerializedPropertyType.RectInt:
                    return $"RectInt(x={property.rectIntValue.x}, y={property.rectIntValue.y}, w={property.rectIntValue.width}, h={property.rectIntValue.height})";
                case SerializedPropertyType.BoundsInt:
                    return $"BoundsInt(pos=({property.boundsIntValue.position.x}, {property.boundsIntValue.position.y}, {property.boundsIntValue.position.z}), size=({property.boundsIntValue.size.x}, {property.boundsIntValue.size.y}, {property.boundsIntValue.size.z}))";
                case SerializedPropertyType.ManagedReference:
                    return property.managedReferenceFullTypename is { Length: > 0 } ? property.managedReferenceFullTypename : "null";
                default:
                    return $"<{property.propertyType}>";
            }
        }

        static FieldInfo[] GetInspectableFields(Type type)
            => fieldCache.GetOrAdd(
                type, static targetType
                    =>
                {
                    var fields = new List<FieldInfo>();
                    var seenNames = new HashSet<string>(StringComparer.Ordinal);
                    for (var current = targetType; current != null && current != typeof(object) && current != typeof(Object); current = current.BaseType)
                    {
                        foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (field.IsDefined(typeof(CompilerGeneratedAttribute), false) || !seenNames.Add(field.Name))
                                continue;

                            fields.Add(field);
                        }
                    }

                    return fields.ToArray();
                }
            );

        static bool IsUnitySerializableField(FieldInfo field)
            => field is { IsStatic: false, IsInitOnly: false, IsNotSerialized: false } &&
               (field.IsPublic || field.IsDefined(typeof(SerializeField), false) || field.IsDefined(typeof(SerializeReference), false));

        static bool TryFormatFieldValue(FieldInfo field, object target, int depth, out string valueText)
        {
            try
            {
                valueText = FormatValue(field.GetValue(target), depth);
                return true;
            }
            catch (Exception exception)
            {
                valueText = FormatUnavailable(exception);
                return false;
            }
        }

    }
}
