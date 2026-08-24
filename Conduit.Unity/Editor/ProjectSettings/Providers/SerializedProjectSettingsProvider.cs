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
    static partial class SerializedProjectSettingsProvider
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
