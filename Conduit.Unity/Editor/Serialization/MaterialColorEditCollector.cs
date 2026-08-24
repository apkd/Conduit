#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace Conduit
{
    static class MaterialColorEditCollector
    {
        internal static void CollectEdits(
            SerializedObject serializedObject,
            Material target,
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, Color> colorEdits,
            HashSet<string> consumedPaths)
        {
            const string collectionPath = "m_SavedProperties.m_Colors";
            if (values.TryGetValue(collectionPath, out var rootValue))
            {
                if (rootValue != SerializedJsonDiff.EmptyArrayValue)
                    throw new InvalidOperationException($"Material overwrite expected '{collectionPath}' to be an array.");

                consumedPaths.Add(collectionPath);
            }

            using var pooledEntries = ConduitPool.GetPooledDictionary<int, MaterialColorEntry>(out var entries);
            foreach (var pair in values)
            {
                if (!SerializedJsonValueDecoder.TryParseIndexedChildPath(pair.Key, collectionPath, out var index, out var childPath))
                    continue;

                consumedPaths.Add(pair.Key);
                if (!entries.TryGetValue(index, out var entry))
                    entry = default;

                switch (childPath)
                {
                    case "first":
                        entry.Name = SerializedJsonValueDecoder.DecodeString(pair.Key, pair.Value);
                        break;
                    case "second":
                        if (pair.Value != SerializedJsonDiff.EmptyObjectValue)
                            throw new InvalidOperationException($"Material overwrite expected '{pair.Key}' to be a color object.");
                        break;
                    case "second.r":
                        entry.R = SerializedJsonValueDecoder.DecodeFloat(pair.Key, pair.Value);
                        break;
                    case "second.g":
                        entry.G = SerializedJsonValueDecoder.DecodeFloat(pair.Key, pair.Value);
                        break;
                    case "second.b":
                        entry.B = SerializedJsonValueDecoder.DecodeFloat(pair.Key, pair.Value);
                        break;
                    case "second.a":
                        entry.A = SerializedJsonValueDecoder.DecodeFloat(pair.Key, pair.Value);
                        break;
                    default:
                        throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");
                }

                entries[index] = entry;
            }

            if (entries.Count == 0)
                return;

            using var pooledIndexes = ConduitPool.GetPooledList<int>(out var indexes);
            foreach (var index in entries.Keys)
                indexes.Add(index);
            indexes.Sort();
            using var pooledSeenNames = ConduitPool.GetPooledSet<string>(out var seenNames);
            foreach (var index in indexes)
            {
                var entry = entries[index];
                if (entry.Name is not { Length: > 0 } propertyName)
                    throw new InvalidOperationException($"Material overwrite requires a 'first' key in '{collectionPath}[{index}]'.");

                if (!entry.HasAnyChannel)
                    throw new InvalidOperationException($"Material overwrite requires at least one color channel in '{collectionPath}[{index}].second'.");

                if (!seenNames.Add(propertyName))
                    throw new InvalidOperationException($"Material overwrite received duplicate key '{propertyName}' in '{collectionPath}'.");

                MaterialShaderPropertyCatalog.ValidatePropertyType(
                    shaderPropertyTypes,
                    propertyName,
                    "color",
                    ShaderPropertyType.Color
                );

                var color = MaterialEditApplier.TryReadSavedColor(serializedObject, propertyName, out var serializedColor)
                    ? serializedColor
                    : target.GetColor(propertyName);
                if (entry.R.HasValue)
                    color.r = entry.R.Value;
                if (entry.G.HasValue)
                    color.g = entry.G.Value;
                if (entry.B.HasValue)
                    color.b = entry.B.Value;
                if (entry.A.HasValue)
                    color.a = entry.A.Value;

                colorEdits[propertyName] = color;
            }
        }
    }
}
