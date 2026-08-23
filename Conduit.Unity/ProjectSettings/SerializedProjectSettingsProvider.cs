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
    // serialized access keeps built-in and package settings version-tolerant without compiling against optional APIs.
    static class SerializedProjectSettingsProvider
    {
        const string ArrayElementMarker = ".Array.data[";
        static readonly int guidPropertyType = Enum.TryParse(
            "GUID",
            out SerializedPropertyType parsedGuidPropertyType
        )
            ? (int)parsedGuidPropertyType
            : -1;
        static readonly PropertyInfo? guidValueProperty = typeof(SerializedProperty).GetProperty(
            "guidValue",
            BindingFlags.Instance | BindingFlags.Public
        );

        internal static void RegisterFile(
            ProjectSettingsRegistry registry,
            string prefix,
            string path,
            Func<string, string?>? mapPath = null)
        {
            if (!File.Exists(path))
                return;

            if (LoadSettingsAsset(path) is not { } target)
                return;

            using var serializedObject = new SerializedObject(target);
            RegisterProperties(
                registry,
                prefix,
                serializedObject,
                mapPath,
                propertyPath => ReadFile(path, propertyPath),
                (propertyPath, value) => WriteFile(path, propertyPath, value),
                propertyPath => RemoveFile(path, propertyPath)
            );
        }

        internal static void RegisterObject(
            ProjectSettingsRegistry registry,
            string prefix,
            Object target,
            Action? save,
            Func<string, string?>? mapPath = null)
        {
            if (target == null)
                return;

            using var serializedObject = new SerializedObject(target);
            RegisterProperties(
                registry,
                prefix,
                serializedObject,
                mapPath,
                propertyPath =>
                {
                    using var current = new SerializedObject(target);
                    return ReadValue(current, propertyPath);
                },
                save == null
                    ? null
                    : (propertyPath, value) => Modify(target, save, propertyPath, value, remove: false),
                save == null
                    ? null
                    : propertyPath => Modify(target, save, propertyPath, null, remove: true)
            );
        }

        static void RegisterProperties(
            ProjectSettingsRegistry registry,
            string prefix,
            SerializedObject serializedObject,
            Func<string, string?>? mapPath,
            Func<string, string> read,
            Action<string, string>? write,
            Action<string>? remove)
        {
            string? Map(string path) => mapPath == null ? ToKey(path) : mapPath(path);

            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
                return;

            do
            {
                string propertyPath = iterator.propertyPath;
                if (ShouldSkip(propertyPath))
                    continue;

                if (iterator.propertyType == SerializedPropertyType.ArraySize)
                {
                    string arrayPath = propertyPath[..^".Array.size".Length];
                    string? relativeArrayKey = Map(arrayPath);
                    if (string.IsNullOrWhiteSpace(relativeArrayKey))
                        continue;

                    string capturedSizePath = propertyPath;
                    registry.Add($"{prefix}.{relativeArrayKey}.count", () => read(capturedSizePath));
                    var array = serializedObject.FindProperty(arrayPath);
                    if (write != null && array != null && IsSupportedArrayElement(array))
                    {
                        int appendIndex = array.arraySize;
                        string appendPath = $"{arrayPath}.Array.data[{appendIndex}]";
                        registry.AddCollectionAppend(
                            $"{prefix}.{relativeArrayKey}.{appendIndex}",
                            () => read(appendPath),
                            value => write(appendPath, value)
                        );
                    }

                    continue;
                }

                if (!IsSupportedLeaf(iterator) && !IsCompoundArrayElement(iterator))
                    continue;

                string? relativeKey = Map(propertyPath);
                if (string.IsNullOrWhiteSpace(relativeKey))
                    continue;

                string capturedPath = propertyPath;
                bool isCollectionElement = IsArrayElementPath(propertyPath);
                var writer = write == null || IsReadOnly(iterator)
                    ? null
                    : new Action<string>(value => write(capturedPath, value));
                string key = $"{prefix}.{relativeKey}";
                if (writer == null)
                    registry.Add(key, () => read(capturedPath));
                else if (isCollectionElement)
                    registry.AddCollectionElement(
                        key,
                        () => read(capturedPath),
                        writer,
                        () => remove!(capturedPath)
                    );
                else
                    registry.Add(key, () => read(capturedPath), writer);
            }
            while (iterator.NextVisible(true));
        }

        internal static string ReadFile(string path, string propertyPath)
        {
            var target = LoadSettingsAsset(path)
                         ?? throw new InvalidOperationException($"Unity could not load '{path}'.");

            using var serializedObject = new SerializedObject(target);
            return ReadValue(serializedObject, propertyPath);
        }

        internal static void WriteFile(string path, string propertyPath, string value)
        {
            var target = LoadSettingsAsset(path)
                         ?? throw new InvalidOperationException($"Unity could not load '{path}'.");
            Modify(target, AssetDatabase.SaveAssets, propertyPath, value, remove: false);
        }

        static void RemoveFile(string path, string propertyPath)
        {
            var target = LoadSettingsAsset(path)
                         ?? throw new InvalidOperationException($"Unity could not load '{path}'.");
            Modify(target, AssetDatabase.SaveAssets, propertyPath, null, remove: true);
        }

        static void Modify(
            Object target,
            Action save,
            string propertyPath,
            string? value,
            bool remove)
        {
            using var serializedObject = new SerializedObject(target);
            serializedObject.Update();
            SerializedProperty? appendedArray = null;
            try
            {
                if (remove)
                    RemoveProperty(Find(serializedObject, propertyPath));
                else
                    WriteProperty(
                        FindForWrite(serializedObject, propertyPath, out appendedArray),
                        value!
                    );
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);
                save();
            }
            catch
            {
                RemoveAppendedElement(appendedArray);
                throw;
            }
        }

        // native managers must be edited through Unity's live asset; deserializing a copy can reinitialize subsystems.
        internal static Object? LoadSettingsAsset(string path) => AssetDatabase.LoadMainAssetAtPath(path);

        static SerializedProperty Find(SerializedObject serializedObject, string path)
            => serializedObject.FindProperty(path)
               ?? throw new InvalidOperationException(
                   $"Serialized setting '{path}' is unavailable in this Unity version."
               );

        static string ReadValue(SerializedObject serializedObject, string path)
        {
            if (serializedObject.FindProperty(path) is { } property)
                return ReadValue(property);

            if (TryParseArrayElementPath(path, out var arrayPath, out var index)
                && serializedObject.FindProperty(arrayPath) is { isArray: true } array
                && index == array.arraySize)
                return "<append>";

            throw new InvalidOperationException(
                $"Serialized setting '{path}' is unavailable in this Unity version."
            );
        }

        static SerializedProperty FindForWrite(
            SerializedObject serializedObject,
            string path,
            out SerializedProperty? appendedArray)
        {
            appendedArray = null;
            if (serializedObject.FindProperty(path) is { } existing)
                return existing;

            if (!TryParseArrayElementPath(path, out var arrayPath, out var index))
                return Find(serializedObject, path);

            var array = Find(serializedObject, arrayPath);
            if (!array.isArray || index != array.arraySize)
                throw new InvalidOperationException(
                    $"Append at index {index} is invalid; "
                    + $"the next index for '{array.propertyPath}' is {array.arraySize}."
                );

            // unity duplicates the previous slot when inserting; reset it so an append never inherits hidden fields.
            array.InsertArrayElementAtIndex(index);
            appendedArray = array;
            var element = array.GetArrayElementAtIndex(index);
            ResetValue(element);
            return element;
        }

        static void RemoveAppendedElement(SerializedProperty? array)
        {
            if (array == null || array.arraySize == 0)
                return;

            int index = array.arraySize - 1;
            array.DeleteArrayElementAtIndex(index);
            if (array.arraySize > index)
                array.DeleteArrayElementAtIndex(index);
        }

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
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        static bool IsSupportedLeaf(SerializedProperty property)
            => IsGuid(property)
               || property.propertyType is SerializedPropertyType.Integer
                   or SerializedPropertyType.Boolean
                   or SerializedPropertyType.Float
                   or SerializedPropertyType.String
                   or SerializedPropertyType.Color
                   or SerializedPropertyType.ObjectReference
                   or SerializedPropertyType.LayerMask
                   or SerializedPropertyType.Enum
                   or SerializedPropertyType.Vector2
                   or SerializedPropertyType.Vector3
                   or SerializedPropertyType.Vector4
                   or SerializedPropertyType.Rect
                   or SerializedPropertyType.AnimationCurve
                   or SerializedPropertyType.Bounds
                   or SerializedPropertyType.Quaternion
                   or SerializedPropertyType.Vector2Int
                   or SerializedPropertyType.Vector3Int
                   or SerializedPropertyType.RectInt
                   or SerializedPropertyType.BoundsInt
                   or SerializedPropertyType.ArraySize
                   or SerializedPropertyType.Gradient
                   or SerializedPropertyType.ExposedReference
                   or SerializedPropertyType.FixedBufferSize
                   or SerializedPropertyType.Hash128
                   or SerializedPropertyType.RenderingLayerMask;

        static bool IsSupportedArrayElement(SerializedProperty array)
        {
            if (array.arraySize > 0)
            {
                var element = array.GetArrayElementAtIndex(0);
                return IsSupportedLeaf(element) || element.propertyType == SerializedPropertyType.Generic;
            }

            string elementType = array.arrayElementType;
            return elementType.Length > 0
                   && !elementType.Contains("managedReference", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsCompoundArrayElement(SerializedProperty property)
            => property.propertyType == SerializedPropertyType.Generic
               && IsArrayElementPath(property.propertyPath);

        static bool IsArrayElementPath(string path)
            => TryGetArrayElementParts(path, out _, out _);

        static bool TryParseArrayElementPath(string path, out string arrayPath, out int index)
        {
            if (TryGetArrayElementParts(path, out var marker, out var indexText)
                && int.TryParse(
                    indexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out index
                ))
            {
                arrayPath = path[..marker];
                return true;
            }

            arrayPath = string.Empty;
            index = 0;
            return false;
        }

        static bool TryGetArrayElementParts(
            string path,
            out int marker,
            out ReadOnlySpan<char> indexText)
        {
            marker = path.LastIndexOf(ArrayElementMarker, StringComparison.Ordinal);
            int indexStart = marker + ArrayElementMarker.Length;
            if (marker <= 0 || indexStart >= path.Length || path[^1] != ']')
            {
                indexText = default;
                return false;
            }

            indexText = path.AsSpan(indexStart, path.Length - indexStart - 1);
            foreach (var character in indexText)
                if (character is < '0' or > '9')
                    return false;

            return indexText.Length > 0;
        }

        static IEnumerable<SerializedProperty> GetDirectChildren(SerializedProperty property)
        {
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            if (!iterator.NextVisible(true))
                yield break;

            while (!SerializedProperty.EqualContents(iterator, end) && iterator.depth > property.depth)
            {
                if (iterator.depth == property.depth + 1)
                    yield return iterator.Copy();
                if (!iterator.NextVisible(false))
                    yield break;
            }
        }

        static bool ShouldSkip(string path)
        {
            string key = ProjectSettingKey.Canonicalize(path);
            var keySpan = key.AsSpan();
            // implementation metadata, obsolete systems, telemetry, and credentials are outside this tool's ownership.
            return key is "m_object_hide_flags" or "m_script"
                   || ContainsToken(keySpan, "serialized") && ContainsToken(keySpan, "version")
                   || ContainsToken(keySpan, "obsolete", matchPrefix: true)
                   || ContainsToken(keySpan, "deprecated", matchPrefix: true)
                   || key is "m_asset_version" or "m_last_material_version"
                   || key.Contains("vr_settings", StringComparison.Ordinal)
                   || key.Contains("virtual_reality", StringComparison.Ordinal)
                   || key.Contains("submit_analytics", StringComparison.Ordinal)
                   || key.Contains("password", StringComparison.Ordinal)
                   || key.Contains("keyalias_pass", StringComparison.Ordinal)
                   || key.Contains("keystore_pass", StringComparison.Ordinal)
                   || key.Contains("credential", StringComparison.Ordinal)
                   || key.Contains("access_token", StringComparison.Ordinal)
                   || key.Contains("auth_token", StringComparison.Ordinal);

            static bool ContainsToken(
                ReadOnlySpan<char> value,
                ReadOnlySpan<char> token,
                bool matchPrefix = false)
            {
                int segmentStart = 0;
                for (int index = 0; index <= value.Length; ++index)
                {
                    if (index < value.Length && value[index] is not ('.' or '_'))
                        continue;

                    var segment = value[segmentStart..index];
                    if (matchPrefix
                            ? segment.StartsWith(token, StringComparison.Ordinal)
                            : segment.Equals(token, StringComparison.Ordinal))
                        return true;
                    segmentStart = index + 1;
                }

                return false;
            }
        }

        static bool IsReadOnly(SerializedProperty property)
        {
            string key = ProjectSettingKey.Canonicalize(property.propertyPath);
            return !property.editable
                   || property.propertyType == SerializedPropertyType.FixedBufferSize
                   || IsGuid(property)
                   || key is "product_guid" or "project_guid"
                   || key.StartsWith("product_guid.", StringComparison.Ordinal)
                   || key.StartsWith("project_guid.", StringComparison.Ordinal);
        }

        static bool IsGuid(SerializedProperty property)
            => guidValueProperty != null && (int)property.propertyType == guidPropertyType;

        internal static string ToKey(string propertyPath)
        {
            Span<char> normalized = propertyPath.Length <= 512
                ? stackalloc char[propertyPath.Length]
                : new char[propertyPath.Length];
            int outputLength = 0;
            int inputIndex = 0;
            while (inputIndex < propertyPath.Length)
            {
                if (propertyPath.AsSpan(inputIndex).StartsWith(ArrayElementMarker, StringComparison.Ordinal))
                {
                    normalized[outputLength++] = '.';
                    inputIndex += ArrayElementMarker.Length;
                    continue;
                }

                if (propertyPath[inputIndex] == ']')
                {
                    inputIndex++;
                    continue;
                }

                if ((outputLength == 0 || normalized[outputLength - 1] == '.')
                    && propertyPath.AsSpan(inputIndex).StartsWith("m_", StringComparison.Ordinal))
                {
                    inputIndex += 2;
                    continue;
                }

                normalized[outputLength++] = propertyPath[inputIndex++];
            }

            return ProjectSettingKey.Canonicalize(normalized[..outputLength]);
        }

        // json utility needs serializable field wrappers for engine-native curve and gradient values.
        [Serializable]
        sealed class AnimationCurveValue
        {
            [SerializeField]
            internal AnimationCurve value = new();
        }

        [Serializable]
        sealed class GradientValue
        {
            [SerializeField]
            internal Gradient value = new();
        }
    }
}
