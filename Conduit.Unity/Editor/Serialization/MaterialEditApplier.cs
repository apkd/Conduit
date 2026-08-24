#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class MaterialEditApplier
    {
        internal static void ApplyDirectEdits(SerializedObject serializedObject, List<MaterialDirectEdit> directEdits)
        {
            foreach (var edit in directEdits)
            {
                var property = serializedObject.FindProperty(edit.Path)
                               ?? throw new InvalidOperationException($"Material overwrite could not resolve '{edit.Path}'.");

                switch (edit.PropertyType)
                {
                    case SerializedPropertyType.String:
                        property.stringValue = SerializedJsonValueDecoder.DecodeString(edit.Path, edit.EncodedValue);
                        break;
                    case SerializedPropertyType.Boolean:
                        property.boolValue = SerializedJsonValueDecoder.DecodeBool(edit.Path, edit.EncodedValue);
                        break;
                    case SerializedPropertyType.Integer:
                    case SerializedPropertyType.Enum:
                        property.intValue = SerializedJsonValueDecoder.DecodeInt(edit.Path, edit.EncodedValue);
                        break;
                    default:
                        throw new InvalidOperationException($"Material overwrite does not support direct property '{edit.Path}'.");
                }
            }
        }

        internal static void ApplyFloatEdits(SerializedObject serializedObject, Dictionary<string, float> floatEdits)
            => ApplyMaterialNamedScalarEdits(
                serializedObject,
                "m_SavedProperties.m_Floats",
                floatEdits,
                static (secondProperty, value) => secondProperty.floatValue = value
            );

        internal static void ApplyIntEdits(SerializedObject serializedObject, Dictionary<string, int> intEdits)
            => ApplyMaterialNamedScalarEdits(
                serializedObject,
                "m_SavedProperties.m_Ints",
                intEdits,
                static (secondProperty, value) => secondProperty.intValue = value
            );

        internal static void ApplyColorEdits(SerializedObject serializedObject, Dictionary<string, Color> colorEdits)
        {
            if (colorEdits.Count == 0)
                return;

            var colorArray = FindMaterialSavedPropertyArray(serializedObject, "m_SavedProperties.m_Colors");
            foreach (var pair in colorEdits)
            {
                var entry = FindOrAddMaterialSavedPropertyEntry(colorArray, pair.Key);
                WriteSerializedColor(
                    entry.FindPropertyRelative("second")
                    ?? throw new InvalidOperationException($"Material overwrite could not resolve color value for '{pair.Key}'."),
                    pair.Value
                );
            }
        }

        static void ApplyMaterialNamedScalarEdits<TValue>(
            SerializedObject serializedObject,
            string collectionPath,
            Dictionary<string, TValue> edits,
            Action<SerializedProperty, TValue> assign)
        {
            if (edits.Count == 0)
                return;

            var arrayProperty = FindMaterialSavedPropertyArray(serializedObject, collectionPath);
            foreach (var pair in edits)
            {
                var entry = FindOrAddMaterialSavedPropertyEntry(arrayProperty, pair.Key);
                assign(
                    entry.FindPropertyRelative("second")
                    ?? throw new InvalidOperationException($"Material overwrite could not resolve '{collectionPath}' value for '{pair.Key}'."),
                    pair.Value
                );
            }
        }

        static SerializedProperty FindMaterialSavedPropertyArray(SerializedObject serializedObject, string collectionPath)
            => serializedObject.FindProperty(collectionPath)
               ?? throw new InvalidOperationException($"Material overwrite could not resolve '{collectionPath}'.");

        static SerializedProperty FindOrAddMaterialSavedPropertyEntry(SerializedProperty arrayProperty, string propertyName)
            => FindMaterialSavedPropertyEntry(arrayProperty, propertyName) ?? AddMaterialSavedPropertyEntry(arrayProperty, propertyName);

        static SerializedProperty AddMaterialSavedPropertyEntry(SerializedProperty arrayProperty, string propertyName)
        {
            var index = arrayProperty.arraySize;
            arrayProperty.InsertArrayElementAtIndex(index);
            var entry = arrayProperty.GetArrayElementAtIndex(index);
            (
                entry.FindPropertyRelative("first")
                ?? throw new InvalidOperationException($"Material overwrite could not resolve key for '{propertyName}'.")
            ).stringValue = propertyName;
            return entry;
        }

        static SerializedProperty? FindMaterialSavedPropertyEntry(SerializedProperty arrayProperty, string propertyName)
        {
            for (int index = 0, count = arrayProperty.arraySize; index < count; index++)
            {
                var entry = arrayProperty.GetArrayElementAtIndex(index);
                if (entry.FindPropertyRelative("first") is not { stringValue: var currentName })
                    continue;

                if (currentName == propertyName)
                    return entry;
            }

            return null;
        }

        internal static bool TryReadSavedColor(SerializedObject serializedObject, string propertyName, out Color color)
        {
            var entry = FindMaterialSavedPropertyEntry(
                FindMaterialSavedPropertyArray(serializedObject, "m_SavedProperties.m_Colors"),
                propertyName
            );
            if (entry == null)
            {
                color = default;
                return false;
            }

            color = ReadSerializedColor(
                entry.FindPropertyRelative("second")
                ?? throw new InvalidOperationException($"Material overwrite could not resolve color value for '{propertyName}'.")
            );
            return true;
        }

        static Color ReadSerializedColor(SerializedProperty colorProperty)
        {
            var color = new Color();
            color.r = (
                colorProperty.FindPropertyRelative("r")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            color.g = (
                colorProperty.FindPropertyRelative("g")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            color.b = (
                colorProperty.FindPropertyRelative("b")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            color.a = (
                colorProperty.FindPropertyRelative("a")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue;
            return color;
        }

        static void WriteSerializedColor(SerializedProperty colorProperty, Color color)
        {
            (
                colorProperty.FindPropertyRelative("r")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.r;
            (
                colorProperty.FindPropertyRelative("g")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.g;
            (
                colorProperty.FindPropertyRelative("b")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.b;
            (
                colorProperty.FindPropertyRelative("a")
                ?? throw new InvalidOperationException("Material overwrite could not resolve a serialized color channel.")
            ).floatValue = color.a;
        }

        internal static void ApplyDisabledShaderPasses(SerializedObject serializedObject, string[] disabledShaderPasses)
        {
            var disabledPasses = serializedObject.FindProperty("disabledShaderPasses")
                               ?? throw new InvalidOperationException("Material overwrite could not resolve 'disabledShaderPasses'.");
            if (!disabledPasses.isArray)
                throw new InvalidOperationException("Material overwrite expected 'disabledShaderPasses' to be an array of strings.");

            disabledPasses.arraySize = disabledShaderPasses.Length;
            for (var index = 0; index < disabledShaderPasses.Length; index++)
                disabledPasses.GetArrayElementAtIndex(index).stringValue = disabledShaderPasses[index];
        }
    }
}
