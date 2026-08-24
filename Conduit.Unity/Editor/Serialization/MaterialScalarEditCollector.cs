#nullable enable

using System;
using System.Collections.Generic;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace Conduit
{
    static class MaterialScalarEditCollector
    {
        internal static void CollectFloatEdits(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, float> floatEdits,
            HashSet<string> consumedPaths)
        {
            CollectMaterialNamedScalarEdits(
                shaderPropertyTypes,
                values,
                floatEdits,
                "m_SavedProperties.m_Floats",
                consumedPaths,
                decode: SerializedJsonValueDecoder.DecodeFloat,
                label: "float",
                supportedType: ShaderPropertyType.Float,
                alternateSupportedType: ShaderPropertyType.Range
            );
        }

        internal static void CollectIntEdits(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, int> intEdits,
            HashSet<string> consumedPaths)
        {
            CollectMaterialNamedScalarEdits(
                shaderPropertyTypes,
                values,
                intEdits,
                "m_SavedProperties.m_Ints",
                consumedPaths,
                decode: SerializedJsonValueDecoder.DecodeInt,
                label: "integer",
                supportedType: ShaderPropertyType.Int
            );
        }

        static void CollectMaterialNamedScalarEdits<TValue>(
            Dictionary<string, ShaderPropertyType> shaderPropertyTypes,
            Dictionary<string, string> values,
            Dictionary<string, TValue> edits,
            string collectionPath,
            HashSet<string> consumedPaths,
            Func<string, string, TValue> decode,
            string label,
            ShaderPropertyType supportedType,
            ShaderPropertyType? alternateSupportedType = null)
        {
            if (values.TryGetValue(collectionPath, out var rootValue))
            {
                if (rootValue != SerializedJsonDiff.EmptyArrayValue)
                    throw new InvalidOperationException($"Material overwrite expected '{collectionPath}' to be an array.");

                consumedPaths.Add(collectionPath);
            }

            using var pooledEntries = ConduitPool.GetPooledDictionary<int, MaterialNamedScalarEntry>(out var entries);
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
                        entry.EncodedValue = pair.Value;
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
                if (entry.Name is not { Length: > 0 } propertyName || entry.EncodedValue is not { Length: > 0 } encodedValue)
                    throw new InvalidOperationException($"Material overwrite requires both 'first' and 'second' in '{collectionPath}[{index}]'.");

                if (!seenNames.Add(propertyName))
                    throw new InvalidOperationException($"Material overwrite received duplicate key '{propertyName}' in '{collectionPath}'.");

                MaterialShaderPropertyCatalog.ValidatePropertyType(
                    shaderPropertyTypes,
                    propertyName,
                    label,
                    supportedType,
                    alternateSupportedType
                );
                edits[propertyName] = decode(
                    $"{collectionPath}[{index}].second",
                    encodedValue
                );
            }
        }
    }
}
