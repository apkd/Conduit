#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ShaderPropertyType = UnityEngine.Rendering.ShaderPropertyType;
using Object = UnityEngine.Object;
using PrefabStage = UnityEditor.SceneManagement.PrefabStage;
using PrefabStageUtility = UnityEditor.SceneManagement.PrefabStageUtility;

namespace Conduit
{
    static class UnityObjectJsonOverwrite
    {
        internal const string UndoName = "Conduit From Json Overwrite";

        internal static Object Apply(
            Object target,
            string json,
            string beforeJson,
            out string? afterJson)
        {
            afterJson = null;
            if (target == null)
                throw new InvalidOperationException("Resolved object was null.");

            if (target is Material material)
                return MaterialJsonOverwrite.Apply(material, json);

            if (TryOverwriteOpenPrefabStageObject(target, json))
                return target;

            if (PrefabUtility.IsPartOfPrefabAsset(target))
                return OverwritePrefabAssetObject(target, json);

            if (target is GameObject or Component)
            {
                afterJson = OverwriteSceneObject(target, json, beforeJson);
                return target;
            }

            OverwritePersistentObject(target, json);
            return target;
        }

        static bool TryOverwriteOpenPrefabStageObject(Object target, string json)
        {
            var gameObject = target.AsGameObject();
            if (gameObject == null)
                return false;

            var prefabStage = PrefabStageUtility.GetPrefabStage(gameObject);
            if (prefabStage == null)
                return false;

            Undo.RecordObject(target, UndoName);
            EditorJsonUtility.FromJsonOverwrite(json, target);
            ApplyOwningGameObjectNameOverwrite(target, json);
            MarkPrefabOverrideIfNeeded(target);
            EditorSceneManager.MarkSceneDirty(prefabStage.scene);
            SavePrefabStage(prefabStage);
            AssetDatabase.SaveAssets();
            return true;
        }

        static Object OverwritePrefabAssetObject(Object target, string json)
        {
            if (AssetDatabase.GetAssetPath(target) is not { Length: > 0 } assetPath)
                throw new InvalidOperationException("Could not resolve prefab asset path for overwrite target.");

            var currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (currentPrefabStage != null && string.Equals(currentPrefabStage.assetPath, assetPath, StringComparison.OrdinalIgnoreCase))
            {
                var stageTarget = RemapToPrefabContents(target, currentPrefabStage.prefabContentsRoot);
                Undo.RecordObject(stageTarget, UndoName);
                EditorJsonUtility.FromJsonOverwrite(json, stageTarget);
                ApplyOwningGameObjectNameOverwrite(stageTarget, json);
                MarkPrefabOverrideIfNeeded(stageTarget);
                EditorSceneManager.MarkSceneDirty(currentPrefabStage.scene);
                SavePrefabStage(currentPrefabStage);
                AssetDatabase.SaveAssets();
                return stageTarget;
            }

            var prefabRoot = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var editableTarget = RemapToPrefabContents(target, prefabRoot);
                Undo.RecordObject(editableTarget, UndoName);
                EditorJsonUtility.FromJsonOverwrite(json, editableTarget);
                ApplyOwningGameObjectNameOverwrite(editableTarget, json);
                MarkPrefabOverrideIfNeeded(editableTarget);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, assetPath);
                AssetDatabase.SaveAssets();
                return ReloadPrefabAssetTarget(target, assetPath);
            }
            finally
            {
                if (prefabRoot != null)
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        static Object ReloadPrefabAssetTarget(Object originalTarget, string assetPath)
        {
            var prefabRoot = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject
                             ?? throw new InvalidOperationException($"Could not reload prefab asset '{assetPath}'.");

            return RemapToPrefabContents(originalTarget, prefabRoot);
        }

        static string OverwriteSceneObject(Object target, string json, string beforeJson)
        {
            var gameObject = target.AsGameObject()
                             ?? throw new InvalidOperationException("Could not resolve the owning scene object.");

            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("Resolved scene object does not belong to a loaded scene.");

            var beforeOwningGameObjectName = gameObject.name;
            Undo.RecordObject(target, UndoName);
            EditorJsonUtility.FromJsonOverwrite(json, target);
            ApplyOwningGameObjectNameOverwrite(target, json);
            MarkPrefabOverrideIfNeeded(target);
            var afterJson = EditorJsonUtility.ToJson(target, true);
            if (beforeJson != afterJson
                || beforeOwningGameObjectName != gameObject.name)
                EditorSceneManager.MarkSceneDirty(scene);

            return afterJson;
        }

        static void OverwritePersistentObject(Object target, string json)
        {
            SerializedJsonValueDecoder.ValidateEditablePersistentAsset(target);

            Undo.RecordObject(target, UndoName);
            EditorJsonUtility.FromJsonOverwrite(json, target);
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();
        }

        static void ApplyOwningGameObjectNameOverwrite(Object target, string json)
        {
            var gameObject = target.AsGameObject();
            if (gameObject == null || !SerializedJsonDiff.TryReadRootNameOverwrite(json, out var name))
                return;

            gameObject.name = name;
        }

        static Object RemapToPrefabContents(Object originalTarget, GameObject prefabContentsRoot)
        {
            switch (originalTarget)
            {
                case GameObject gameObject:
                    return FindGameObjectByPath(prefabContentsRoot.transform, BuildRelativeTransformPath(gameObject.transform));
                case Component component:
                {
                    var mappedGameObject = FindGameObjectByPath(prefabContentsRoot.transform, BuildRelativeTransformPath(component.transform));
                    using var pooledOriginalComponents = ConduitPool.GetPooledList<Component>(out var originalComponents);
                    component.gameObject.GetComponents(component.GetType(), originalComponents);
                    var componentIndex = originalComponents.IndexOf(component);
                    if (componentIndex < 0)
                        throw new InvalidOperationException($"Could not determine component index for '{component.GetType().FullName}'.");

                    using var pooledMappedComponents = ConduitPool.GetPooledList<Component>(out var mappedComponents);
                    mappedGameObject.GetComponents(component.GetType(), mappedComponents);
                    if (componentIndex >= mappedComponents.Count)
                        throw new InvalidOperationException($"Could not remap component '{component.GetType().FullName}' inside prefab contents.");

                    return mappedComponents[componentIndex];
                }
                default:
                    return originalTarget;
            }
        }

        static GameObject FindGameObjectByPath(Transform prefabRoot, string relativePath)
        {
            if (relativePath is not { Length: > 0 })
                return prefabRoot.gameObject;

            return prefabRoot.Find(relativePath) is { } current
                ? current.gameObject
                : throw new InvalidOperationException($"Could not find prefab object path '{relativePath}'.");
        }

        static string BuildRelativeTransformPath(Transform transform)
        {
            using var pooledSegments = ConduitPool.GetPooledList<string>(out var segments);
            for (var current = transform; current != null && current.parent != null; current = current.parent)
                segments.Add(current.name);

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            for (var index = segments.Count - 1; index >= 0; --index)
            {
                if (builder.Length > 0)
                    builder.Append('/');
                builder.Append(segments[index]);
            }
            return builder.ToString();
        }

        static void MarkPrefabOverrideIfNeeded(Object target)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(target))
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        }

        static void SavePrefabStage(PrefabStage prefabStage)
        {
            if (prefabStage.prefabContentsRoot == null || string.IsNullOrWhiteSpace(prefabStage.assetPath))
                throw new InvalidOperationException("Current prefab stage does not expose a saveable prefab root.");

            PrefabUtility.SaveAsPrefabAsset(prefabStage.prefabContentsRoot, prefabStage.assetPath);
        }

    }
}
