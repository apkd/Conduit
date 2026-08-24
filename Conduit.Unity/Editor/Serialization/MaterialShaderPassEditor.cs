#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class MaterialShaderPassEditor
    {
        internal static string[]? CollectDisabledPasses(
            SerializedObject serializedObject,
            Material target,
            Dictionary<string, string> values,
            HashSet<string> consumedPaths)
        {
            if (values.TryGetValue("disabledShaderPasses", out var rootValue))
            {
                if (rootValue != SerializedJsonDiff.EmptyArrayValue)
                    throw new InvalidOperationException("Material overwrite expected 'disabledShaderPasses' to be an array of strings.");

                consumedPaths.Add("disabledShaderPasses");
                return Array.Empty<string>();
            }

            using var pooledDisabledPasses = ConduitPool.GetPooledDictionary<int, string>(out var disabledPassesByIndex);
            foreach (var pair in values)
            {
                if (!SerializedJsonValueDecoder.TryParseIndexedChildPath(
                        pair.Key,
                        "disabledShaderPasses",
                        out var index,
                        out var childPath
                    ))
                    continue;

                if (childPath.Length != 0)
                    throw new InvalidOperationException($"Material overwrite does not support path '{pair.Key}'.");

                var passName = SerializedJsonValueDecoder.DecodeString(pair.Key, pair.Value);
                if (!disabledPassesByIndex.TryAdd(index, passName))
                    throw new InvalidOperationException($"Material overwrite received duplicate array index for '{pair.Key}'.");

                consumedPaths.Add(pair.Key);
            }

            if (disabledPassesByIndex.Count == 0)
                return null;

            using var pooledPassNames = ConduitPool.GetPooledDictionary<string, string>(out var canonicalPassNames);
            GetMaterialShaderPassNames(serializedObject, target, canonicalPassNames);
            using var pooledIndexes = ConduitPool.GetPooledList<int>(out var indexes);
            foreach (var index in disabledPassesByIndex.Keys)
                indexes.Add(index);
            indexes.Sort();
            var desiredPasses = new string[indexes.Count];
            using var pooledSeenNames = ConduitPool.GetPooledSet<string>(out var seenNames);
            for (var arrayIndex = 0; arrayIndex < indexes.Count; arrayIndex++)
            {
                var requestedPassName = disabledPassesByIndex[indexes[arrayIndex]];
                var normalizedPassName = NormalizeMaterialShaderPassName(requestedPassName);
                if (!canonicalPassNames.TryGetValue(normalizedPassName, out var passName))
                    throw new InvalidOperationException($"Material overwrite does not support shader pass '{requestedPassName}'.");

                if (!seenNames.Add(normalizedPassName))
                    throw new InvalidOperationException($"Material overwrite received duplicate disabled shader pass '{requestedPassName}'.");

                desiredPasses[arrayIndex] = passName;
            }

            return desiredPasses;
        }

        static void GetMaterialShaderPassNames(
            SerializedObject serializedObject,
            Material target,
            Dictionary<string, string> passNames)
        {
            AddRuntimeMaterialShaderPassNames(target, passNames);
            AddSerializedMaterialShaderPassNames(serializedObject, passNames);
        }

        static void AddRuntimeMaterialShaderPassNames(Material target, Dictionary<string, string> passNames)
        {
            for (int index = 0, passCount = target.passCount; index < passCount; index++)
            {
                if (target.GetPassName(index) is not { Length: > 0 } passName)
                    continue;

                passNames.TryAdd(NormalizeMaterialShaderPassName(passName), passName);
            }
        }

        static string NormalizeMaterialShaderPassName(string passName) => passName.ToUpperInvariant();

        static void AddSerializedMaterialShaderPassNames(SerializedObject serializedObject, Dictionary<string, string> passNames)
        {
            var disabledPasses = serializedObject.FindProperty("disabledShaderPasses");
            if (disabledPasses is not { isArray: true })
                return;

            for (int index = 0, count = disabledPasses.arraySize; index < count; index++)
            {
                var passName = disabledPasses.GetArrayElementAtIndex(index).stringValue;
                if (string.IsNullOrWhiteSpace(passName))
                    continue;

                passNames[NormalizeMaterialShaderPassName(passName)] = passName;
            }
        }
    }
}
