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
        static void ResetValue(SerializedProperty property)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                property.arraySize = 0;
                return;
            }

            if (IsGuid(property))
            {
                var valueProperty = guidValueProperty!;
                valueProperty.SetValue(
                    property,
                    Activator.CreateInstance(valueProperty.PropertyType)
                );
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    property.longValue = 0;
                    return;
                case SerializedPropertyType.Boolean:
                    property.boolValue = false;
                    return;
                case SerializedPropertyType.Float:
                    property.doubleValue = 0;
                    return;
                case SerializedPropertyType.String:
                    property.stringValue = string.Empty;
                    return;
                case SerializedPropertyType.Color:
                    property.colorValue = default;
                    return;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = null;
                    return;
                case SerializedPropertyType.ExposedReference:
                    property.exposedReferenceValue = null;
                    return;
                case SerializedPropertyType.Enum:
                    property.enumValueIndex = 0;
                    return;
                case SerializedPropertyType.Vector2:
                    property.vector2Value = default;
                    return;
                case SerializedPropertyType.Vector3:
                    property.vector3Value = default;
                    return;
                case SerializedPropertyType.Vector4:
                    property.vector4Value = default;
                    return;
                case SerializedPropertyType.Rect:
                    property.rectValue = default;
                    return;
                case SerializedPropertyType.AnimationCurve:
                    property.animationCurveValue = new();
                    return;
                case SerializedPropertyType.Bounds:
                    property.boundsValue = default;
                    return;
                case SerializedPropertyType.Quaternion:
                    property.quaternionValue = Quaternion.identity;
                    return;
                case SerializedPropertyType.Vector2Int:
                    property.vector2IntValue = default;
                    return;
                case SerializedPropertyType.Vector3Int:
                    property.vector3IntValue = default;
                    return;
                case SerializedPropertyType.RectInt:
                    property.rectIntValue = default;
                    return;
                case SerializedPropertyType.BoundsInt:
                    property.boundsIntValue = default;
                    return;
                case SerializedPropertyType.Gradient:
                    property.gradientValue = new();
                    return;
                case SerializedPropertyType.Hash128:
                    property.hash128Value = default;
                    return;
                case SerializedPropertyType.RenderingLayerMask:
                    property.uintValue = 0;
                    return;
            }

            foreach (var child in GetDirectChildren(property))
                ResetValue(child);
        }

        static void WriteProperty(SerializedProperty property, string value)
        {
            if (!IsSupportedLeaf(property))
            {
                ApplyJson(property, ConduitSimpleJson.ParseValue(value));
                return;
            }

            try
            {
                if (IsGuid(property))
                {
                    string text = (string)ProjectSettingValueCodec.Parse(value, typeof(string))!;
                    var valueProperty = guidValueProperty!;
                    var guid = Activator.CreateInstance(valueProperty.PropertyType, text)
                               ?? throw new FormatException($"Could not construct GUID '{text}'.");
                    valueProperty.SetValue(property, guid);
                    return;
                }

                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        property.longValue = long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        break;
                    case SerializedPropertyType.Boolean:
                        property.boolValue = (bool)ProjectSettingValueCodec.Parse(value, typeof(bool))!;
                        break;
                    case SerializedPropertyType.Float:
                        property.doubleValue = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                        break;
                    case SerializedPropertyType.String:
                        property.stringValue = (string?)ProjectSettingValueCodec.Parse(value, typeof(string));
                        break;
                    case SerializedPropertyType.Color:
                        property.colorValue = JsonUtility.FromJson<Color>(value);
                        break;
                    case SerializedPropertyType.ObjectReference:
                        property.objectReferenceValue = value == "null"
                            ? null
                            : ProjectSettingValueCodec.ParseObjectReference(
                                value,
                                ResolveObjectReferenceType(property)
                            );
                        break;
                    case SerializedPropertyType.ExposedReference:
                        property.exposedReferenceValue = value == "null"
                            ? null
                            : ProjectSettingValueCodec.ParseObjectReference(
                                value,
                                ResolveObjectReferenceType(property)
                            );
                        break;
                    case SerializedPropertyType.LayerMask:
                        property.intValue = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        break;
                    case SerializedPropertyType.Enum:
                        WriteEnum(property, (string)ProjectSettingValueCodec.Parse(value, typeof(string))!);
                        break;
                    case SerializedPropertyType.Vector2:
                        property.vector2Value = JsonUtility.FromJson<Vector2>(value);
                        break;
                    case SerializedPropertyType.Vector3:
                        property.vector3Value = JsonUtility.FromJson<Vector3>(value);
                        break;
                    case SerializedPropertyType.Vector4:
                        property.vector4Value = JsonUtility.FromJson<Vector4>(value);
                        break;
                    case SerializedPropertyType.Rect:
                        property.rectValue = JsonUtility.FromJson<Rect>(value);
                        break;
                    case SerializedPropertyType.Character:
                        property.intValue = (char)ProjectSettingValueCodec.Parse(value, typeof(char))!;
                        break;
                    case SerializedPropertyType.AnimationCurve:
                        property.animationCurveValue = JsonUtility.FromJson<AnimationCurveValue>(value).value;
                        break;
                    case SerializedPropertyType.Bounds:
                        property.boundsValue = JsonUtility.FromJson<Bounds>(value);
                        break;
                    case SerializedPropertyType.Quaternion:
                        property.quaternionValue = JsonUtility.FromJson<Quaternion>(value);
                        break;
                    case SerializedPropertyType.Vector2Int:
                        property.vector2IntValue = JsonUtility.FromJson<Vector2Int>(value);
                        break;
                    case SerializedPropertyType.Vector3Int:
                        property.vector3IntValue = JsonUtility.FromJson<Vector3Int>(value);
                        break;
                    case SerializedPropertyType.RectInt:
                        property.rectIntValue = JsonUtility.FromJson<RectInt>(value);
                        break;
                    case SerializedPropertyType.BoundsInt:
                        property.boundsIntValue = JsonUtility.FromJson<BoundsInt>(value);
                        break;
                    case SerializedPropertyType.Gradient:
                        property.gradientValue = JsonUtility.FromJson<GradientValue>(value).value;
                        break;
                    case SerializedPropertyType.Hash128:
                        property.hash128Value = Hash128.Parse(
                            (string)ProjectSettingValueCodec.Parse(value, typeof(string))!
                        );
                        break;
                    case SerializedPropertyType.RenderingLayerMask:
                        property.uintValue = uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Serialized setting '{property.propertyPath}' has unsupported "
                            + $"type {property.propertyType}."
                        );
                }
            }
            catch (Exception exception) when (
                exception is FormatException
                    or OverflowException
                    or ArgumentException
                    or TargetInvocationException)
            {
                throw new FormatException(
                    $"Could not parse '{value}' for serialized setting "
                    + $"'{property.propertyPath}' ({property.propertyType}).",
                    exception
                );
            }
        }

        static void ApplyJson(SerializedProperty property, ConduitSimpleJson.JsonValue? value)
        {
            if (IsSupportedLeaf(property))
            {
                WriteProperty(property, ConduitSimpleJson.SerializeValue(value));
                return;
            }

            if (property.isArray)
            {
                if (value is not ConduitSimpleJson.JsonArrayValue arrayValue)
                    throw new FormatException($"'{property.propertyPath}' requires a JSON array.");
                property.arraySize = arrayValue.Items.Count;
                for (int index = 0, count = arrayValue.Items.Count; index < count; ++index)
                    ApplyJson(property.GetArrayElementAtIndex(index), arrayValue.Items[index]);
                return;
            }

            if (value is not ConduitSimpleJson.JsonObjectValue objectValue)
                throw new FormatException($"'{property.propertyPath}' requires a JSON object.");

            var children = GetDirectChildren(property)
                .ToDictionary(child => ToKey(child.name), StringComparer.Ordinal);
            foreach (var field in objectValue.Properties)
            {
                string key = ProjectSettingKey.Canonicalize(field.Key);
                if (!children.TryGetValue(key, out var child))
                    throw new FormatException(
                        $"Unknown field '{field.Key}' for serialized setting '{property.propertyPath}'."
                    );
                ApplyJson(child, field.Value);
            }
        }

        static void RemoveProperty(SerializedProperty property)
        {
            if (!TryParseArrayElementPath(property.propertyPath, out var arrayPath, out var index))
                throw new InvalidOperationException(
                    $"Serialized setting '{property.propertyPath}' is not a collection element."
                );

            var parent = property.serializedObject.FindProperty(arrayPath);
            if (parent == null || !parent.isArray)
                throw new InvalidOperationException(
                    $"Serialized setting '{property.propertyPath}' has no collection parent."
                );

            parent.DeleteArrayElementAtIndex(index);
            if (index < parent.arraySize
                && parent.GetArrayElementAtIndex(index).propertyType == SerializedPropertyType.ObjectReference)
                parent.DeleteArrayElementAtIndex(index); // unity clears object references before removing their slot
        }

        static void WriteEnum(SerializedProperty property, string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue))
            {
                property.intValue = numericValue;
                return;
            }

            for (int candidate = 0, count = property.enumNames.Length; candidate < count; ++candidate)
                if (string.Equals(property.enumNames[candidate], value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.enumDisplayNames[candidate], value, StringComparison.OrdinalIgnoreCase))
                {
                    property.enumValueIndex = candidate;
                    return;
                }

            throw new FormatException(
                $"Unknown value '{value}'. Expected one of: {string.Join(", ", property.enumNames)}."
            );
        }

        static Type ResolveObjectReferenceType(SerializedProperty property)
        {
            var current = property.propertyType == SerializedPropertyType.ExposedReference
                ? property.exposedReferenceValue
                : property.objectReferenceValue;
            if (current != null)
                return current.GetType();

            string typeName = property.type;
            int marker = typeName.IndexOf('$');
            int end = typeName.LastIndexOf('>');
            if (marker >= 0 && end > marker)
                typeName = typeName[(marker + 1)..end];
            else if (typeName.StartsWith("PPtr<", StringComparison.Ordinal) && end > 5)
                typeName = typeName[5..end];

            foreach (var type in TypeCache.GetTypesDerivedFrom<Object>())
                if (type.Name == typeName || type.FullName == typeName)
                    return type;

            return typeof(Object);
        }

    }
}

