#nullable enable

using System;
using UnityEditor;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;

namespace Conduit
{
    static class MaterialJsonOverwrite
    {
        internal static Material Apply(Material target, string json)
        {
            SerializedJsonValueDecoder.ValidateEditablePersistentAsset(target);
            using var pooledValues = ConduitPool.GetPooledDictionary<string, string>(out var values);
            if (!SerializedJsonDiff.TryFlatten(json, values))
                throw new InvalidOperationException("Material JSON payload was invalid.");

            using var serializedObject = new SerializedObject(target);
            using var pooledShaderPropertyTypes = ConduitPool.GetPooledDictionary<string, ShaderPropertyType>(out var shaderPropertyTypes);
            using var pooledDirectEdits = ConduitPool.GetPooledList<MaterialDirectEdit>(out var directEdits);
            using var pooledTagEdits = ConduitPool.GetPooledDictionary<string, string>(out var tagEdits);
            using var pooledFloatEdits = ConduitPool.GetPooledDictionary<string, float>(out var floatEdits);
            using var pooledIntEdits = ConduitPool.GetPooledDictionary<string, int>(out var intEdits);
            using var pooledColorEdits = ConduitPool.GetPooledDictionary<string, Color>(out var colorEdits);
            using var pooledConsumedPaths = ConduitPool.GetPooledSet<string>(out var consumedPaths);
            MaterialShaderPropertyCatalog.GetPropertyTypes(target, shaderPropertyTypes);

            MaterialEditCollector.CollectDirectEdits(serializedObject, values, directEdits, consumedPaths);
            MaterialEditCollector.CollectTagEdits(values, tagEdits, consumedPaths);
            var disabledShaderPasses = MaterialShaderPassEditor.CollectDisabledPasses(
                serializedObject,
                target,
                values,
                consumedPaths
            );
            MaterialScalarEditCollector.CollectFloatEdits(shaderPropertyTypes, values, floatEdits, consumedPaths);
            MaterialScalarEditCollector.CollectIntEdits(shaderPropertyTypes, values, intEdits, consumedPaths);
            MaterialColorEditCollector.CollectEdits(
                serializedObject,
                target,
                shaderPropertyTypes,
                values,
                colorEdits,
                consumedPaths
            );
            MaterialEditCollector.EnsureAllPathsWereConsumed(values, consumedPaths);

            if (directEdits.Count == 0
                && tagEdits.Count == 0
                && disabledShaderPasses == null
                && floatEdits.Count == 0
                && intEdits.Count == 0
                && colorEdits.Count == 0)
                return target;

            Undo.RecordObject(target, UnityObjectJsonOverwrite.UndoName);
            MaterialEditApplier.ApplyDirectEdits(serializedObject, directEdits);
            MaterialEditApplier.ApplyFloatEdits(serializedObject, floatEdits);
            MaterialEditApplier.ApplyIntEdits(serializedObject, intEdits);
            MaterialEditApplier.ApplyColorEdits(serializedObject, colorEdits);
            if (disabledShaderPasses != null)
                MaterialEditApplier.ApplyDisabledShaderPasses(serializedObject, disabledShaderPasses);
            serializedObject.ApplyModifiedPropertiesWithoutUndo();

            foreach (var pair in tagEdits)
                target.SetOverrideTag(pair.Key, pair.Value);

            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
            var assetPath = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrWhiteSpace(assetPath))
                return target;

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Material>(assetPath) ?? target;
        }
    }
}
