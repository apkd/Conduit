#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    // serialized access keeps built-in and package settings version-tolerant without compiling against optional APIs.
    static class SerializedProjectSettingsProvider
    {
        static readonly Regex arrayElementPattern = new(
            @"^(?<array>.+)\.Array\.data\[(?<index>\d+)\]$",
            RegexOptions.CultureInvariant
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
                bool isCollectionElement = arrayElementPattern.IsMatch(propertyPath);
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

            var match = arrayElementPattern.Match(path);
            if (match.Success
                && serializedObject.FindProperty(match.Groups["array"].Value) is { isArray: true } array
                && int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture) == array.arraySize)
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

            var match = arrayElementPattern.Match(path);
            if (!match.Success)
                return Find(serializedObject, path);

            var array = Find(serializedObject, match.Groups["array"].Value);
            int index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
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
                case SerializedPropertyType.GUID:
                    property.guidValue = default;
                    return;
            }

            foreach (var child in GetDirectChildren(property))
                ResetValue(child);
        }

        static string ReadProperty(SerializedProperty property)
            => property.propertyType switch
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
                SerializedPropertyType.RenderingLayerMask => property.uintValue.ToString(CultureInfo.InvariantCulture),
                SerializedPropertyType.GUID            => property.guidValue.ToString(),
                _ => throw new NotSupportedException(
                    $"Serialized setting '{property.propertyPath}' has unsupported type {property.propertyType}."
                ),
            };

        static string ReadValue(SerializedProperty property)
            => IsSupportedLeaf(property)
                ? ReadProperty(property)
                : SerializeCompound(property);

        static string SerializeCompound(SerializedProperty property)
        {
            if (property.isArray)
            {
                var values = new string[property.arraySize];
                for (int index = 0, count = values.Length; index < count; ++index)
                    values[index] = SerializeJson(property.GetArrayElementAtIndex(index));
                return "[" + string.Join(",", values) + "]";
            }

            var fields = new List<string>();
            foreach (var child in GetDirectChildren(property))
                fields.Add(
                    ConduitSimpleJson.Quote(ToKey(child.name))
                    + ":"
                    + SerializeJson(child)
                );
            return "{" + string.Join(",", fields) + "}";
        }

        static string SerializeJson(SerializedProperty property)
        {
            if (!IsSupportedLeaf(property))
                return SerializeCompound(property);

            return property.propertyType switch
            {
                SerializedPropertyType.String => ConduitSimpleJson.Quote(property.stringValue),
                SerializedPropertyType.Character => ConduitSimpleJson.Quote(((char)property.intValue).ToString()),
                SerializedPropertyType.Enum => ConduitSimpleJson.Quote(ReadProperty(property)),
                SerializedPropertyType.ObjectReference => property.objectReferenceValue == null
                    ? "null"
                    : FormatReferenceJson(property.objectReferenceValue),
                SerializedPropertyType.ExposedReference => property.exposedReferenceValue == null
                    ? "null"
                    : FormatReferenceJson(property.exposedReferenceValue),
                SerializedPropertyType.Hash128 or SerializedPropertyType.GUID
                    => ConduitSimpleJson.Quote(ReadProperty(property)),
                _ => ReadProperty(property),
            };

            static string FormatReferenceJson(Object value)
            {
                string formatted = ProjectSettingValueCodec.FormatObjectReference(value);
                return formatted.StartsWith("{", StringComparison.Ordinal)
                    ? formatted
                    : ConduitSimpleJson.Quote(formatted);
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
                    case SerializedPropertyType.GUID:
                        property.guidValue = new UnityEngine.GUID(
                            (string)ProjectSettingValueCodec.Parse(value, typeof(string))!
                        );
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Serialized setting '{property.propertyPath}' has unsupported "
                            + $"type {property.propertyType}."
                        );
                }
            }
            catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
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
            var match = arrayElementPattern.Match(property.propertyPath);
            if (!match.Success)
                throw new InvalidOperationException(
                    $"Serialized setting '{property.propertyPath}' is not a collection element."
                );

            var parent = property.serializedObject.FindProperty(match.Groups["array"].Value);
            if (parent == null || !parent.isArray)
                throw new InvalidOperationException(
                    $"Serialized setting '{property.propertyPath}' has no collection parent."
                );

            int index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
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
            => property.propertyType is SerializedPropertyType.Integer
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
                   or SerializedPropertyType.RenderingLayerMask
                   or SerializedPropertyType.GUID;

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
               && arrayElementPattern.IsMatch(property.propertyPath);

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
            var segments = ProjectSettingKey.Tokens(key);
            // implementation metadata, obsolete systems, telemetry, and credentials are outside this tool's ownership.
            return key is "m_object_hide_flags" or "m_script"
                   || Array.IndexOf(segments, "serialized") >= 0 && Array.IndexOf(segments, "version") >= 0
                   || segments.Any(static segment => segment.StartsWith("obsolete", StringComparison.Ordinal)
                                                      || segment.StartsWith("deprecated", StringComparison.Ordinal))
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
        }

        static bool IsReadOnly(SerializedProperty property)
        {
            string key = ProjectSettingKey.Canonicalize(property.propertyPath);
            return !property.editable
                   || property.propertyType is SerializedPropertyType.FixedBufferSize or SerializedPropertyType.GUID
                   || key is "product_guid" or "project_guid";
        }

        internal static string ToKey(string propertyPath)
        {
            string path = propertyPath
                .Replace(".Array.data[", ".", StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal);
            var segments = path.Split('.');
            for (int index = 0, count = segments.Length; index < count; ++index)
                if (segments[index].StartsWith("m_", StringComparison.Ordinal))
                    segments[index] = segments[index][2..];

            return ProjectSettingKey.Canonicalize(string.Join(".", segments));
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
