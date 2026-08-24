#nullable enable

using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class ConduitObjectJsonUtility
    {
        internal static string ToJson(string query)
        {
            var matches = ConduitSearchUtility.Resolve(query);
            if (matches.Count == 0)
                return ConduitSearchUtility.FormatNoMatches(query);

            if (matches.Count > 1)
                return ConduitSearchUtility.FormatMatches(matches, includeHint: true);

            var target = matches[0].RequireTarget();
            if (TryGetSceneAssetPath(target, out var sceneAssetPath))
            {
                throw new InvalidOperationException(
                    $"Target scene '{sceneAssetPath}' cannot be safely and sensibly converted to JSON. " +
                    "Use the `show` tool to display a compact representation of the scene. " +
                    "(Note that the scene needs to be opened to show its contents.) " +
                    "After that, you can use `to_json` and `from_json_overwrite` targeting specific scene objects."
                );
            }

            return EditorJsonUtility.ToJson(target, true);
        }

        internal static string FromJsonOverwrite(string query, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("JSON payload was empty.");

            var matches = ConduitSearchUtility.Resolve(query);
            if (matches.Count == 0)
                return ConduitSearchUtility.FormatNoMatches(query);

            if (matches.Count > 1)
                return ConduitSearchUtility.FormatMatches(matches, includeHint: true);

            var target = matches[0].RequireTarget();
            if (ShouldRejectSceneObjectOverwrite(EditorApplication.isPlaying, target))
                throw new InvalidOperationException(
                    "`from_json_overwrite` cannot modify Editor scene objects during play mode."
                );

            var normalizedJson = NormalizeOverwriteJson(target, json);
            var beforeJson = EditorJsonUtility.ToJson(target, true);
            var beforeOwningGameObjectName = SerializedJsonDiff.GetComparableOwningGameObjectName(
                target,
                normalizedJson
            );
            var updatedTarget = UnityObjectJsonOverwrite.Apply(target, normalizedJson, beforeJson, out var afterJson);
            afterJson ??= EditorJsonUtility.ToJson(updatedTarget, true);
            using var pooledPaths = ConduitPool.GetPooledList<string>(out var changedPaths);
            SerializedJsonDiff.CollectChangedPaths(beforeJson, afterJson, changedPaths);
            SerializedJsonDiff.AddOwningGameObjectNameChangeIfNeeded(
                updatedTarget,
                beforeOwningGameObjectName,
                changedPaths
            );
            return SerializedJsonDiff.FormatChangedPathList(changedPaths);
        }

        internal static bool ShouldRejectSceneObjectOverwrite(bool isPlaying, Object target) =>
            isPlaying
            && target is GameObject or Component
            && !EditorUtility.IsPersistent(target);

        static string NormalizeOverwriteJson(Object target, string json)
        {
            if (!TypedJsonWrapper.TryUnwrap(target, json, out var unwrappedJson, out var wrapperTypeName))
                return json;

            if (wrapperTypeName != null)
                throw new InvalidOperationException(
                    $"JSON wrapper '{wrapperTypeName}' does not match target type '{target.GetType().Name}'."
                );

            return target is Material ? unwrappedJson : json;
        }

        static bool TryGetSceneAssetPath(Object target, out string sceneAssetPath)
        {
            sceneAssetPath = AssetDatabase.GetAssetPath(target);
            return target != null
                   && EditorUtility.IsPersistent(target)
                   && sceneAssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
        }
    }
}
