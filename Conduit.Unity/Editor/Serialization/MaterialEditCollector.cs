#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;

namespace Conduit
{
    static class MaterialEditCollector
    {
        static readonly HashSet<string> supportedDirectProperties = new(StringComparer.Ordinal)
        {
            "m_Name",
            "m_LightmapFlags",
            "m_EnableInstancingVariants",
            "m_DoubleSidedGI",
            "m_CustomRenderQueue",
            "m_AllowLocking",
        };

        internal static void CollectDirectEdits(
            SerializedObject serializedObject,
            Dictionary<string, string> values,
            List<MaterialDirectEdit> directEdits,
            HashSet<string> consumedPaths)
        {
            foreach (var pair in values)
            {
                if (!supportedDirectProperties.Contains(pair.Key))
                    continue;

                if (pair.Key.IndexOf('.') >= 0 || pair.Key.IndexOf('[') >= 0)
                    continue;

                var property = serializedObject.FindProperty(pair.Key)
                               ?? throw new InvalidOperationException($"Material overwrite could not resolve '{pair.Key}'.");
                SerializedJsonValueDecoder.ValidateDirectMaterialProperty(pair.Key, pair.Value, property.propertyType);
                directEdits.Add(
                    new(pair.Key, pair.Value, property.propertyType)
                );
                consumedPaths.Add(pair.Key);
            }
        }

        internal static void CollectTagEdits(
            Dictionary<string, string> values,
            Dictionary<string, string> tagEdits,
            HashSet<string> consumedPaths)
        {
            if (values.TryGetValue("stringTagMap", out var rootValue))
            {
                if (rootValue != SerializedJsonDiff.EmptyObjectValue)
                    throw new InvalidOperationException("Material overwrite expected 'stringTagMap' to be an object.");

                consumedPaths.Add("stringTagMap");
            }

            const string prefix = "stringTagMap.";
            foreach (var pair in values)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var tagName = pair.Key[prefix.Length..];
                if (tagName.Length == 0 || tagName.IndexOf('.') >= 0 || tagName.IndexOf('[') >= 0)
                    throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");

                tagEdits[tagName] = SerializedJsonValueDecoder.DecodeString(pair.Key, pair.Value);
                consumedPaths.Add(pair.Key);
            }
        }

        internal static void EnsureAllPathsWereConsumed(Dictionary<string, string> values, HashSet<string> consumedPaths)
        {
            foreach (var path in values.Keys)
                if (!consumedPaths.Contains(path))
                    throw new InvalidOperationException($"Material overwrite does not support path '{path}'.");
        }
    }
}
