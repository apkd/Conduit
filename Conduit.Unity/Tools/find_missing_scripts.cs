#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Conduit
{
    static class find_missing_scripts
    {
        public static string FindMissingScripts(string assetPattern)
        {
            if (ConduitAssetPathUtility.ExpandAssetPaths(assetPattern, ".prefab", ".unity") is not { Length: > 0 } assetPaths)
                return $"No scenes or prefabs matched '{assetPattern}'.";

            using var pooledHits = ConduitUtility.GetPooledList<MissingScriptHit>(out var hits);
            foreach (var assetPath in assetPaths)
            {
                if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    ScanPrefab(assetPath, hits);
                else
                    ScanScene(assetPath, hits);
            }

            if (hits.Count == 0)
                return $"No missing scripts found in {assetPaths.Length} asset(s).";

            var totalMissingScriptCount = 0;
            foreach (var hit in hits)
                totalMissingScriptCount += hit.MissingScriptCount;

            hits.Sort(static (left, right) =>
                {
                    var assetComparison = StringComparer.OrdinalIgnoreCase.Compare(left.AssetPath, right.AssetPath);
                    return assetComparison != 0
                        ? assetComparison
                        : StringComparer.OrdinalIgnoreCase.Compare(left.ObjectPath, right.ObjectPath);
                }
            );

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine($"Scanned assets: {assetPaths.Length}");
            builder.AppendLine($"Missing script hits: {totalMissingScriptCount}");
            builder.AppendLine();

            string? currentAssetPath = null;
            foreach (var hit in hits)
            {
                if (!string.Equals(currentAssetPath, hit.AssetPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (currentAssetPath != null)
                        builder.AppendLine();

                    currentAssetPath = hit.AssetPath;
                    builder.AppendLine($"[{hit.AssetKind}] {currentAssetPath}");
                }

                builder.Append($"- {hit.ObjectPath} (missing_scripts={hit.MissingScriptCount}");
                if (!string.IsNullOrWhiteSpace(hit.NearestPrefabAssetPath)
                    && !string.Equals(hit.NearestPrefabAssetPath, hit.AssetPath, StringComparison.OrdinalIgnoreCase))
                    builder.Append($", prefab_source={hit.NearestPrefabAssetPath}");

                builder.AppendLine(")");
            }

            return builder.TrimEnd().ToString();
        }

        static void ScanScene(string scenePath, List<MissingScriptHit> hits)
        {
            var scene = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                using var pooledRoots = ConduitUtility.GetPooledList<GameObject>(out var roots);
                scene.GetRootGameObjects(roots);
                foreach (var root in roots)
                    ScanHierarchy(root, scenePath, "Scene", hits);
            }
            finally
            {
                if (scene.IsValid())
                    EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        static void ScanPrefab(string prefabPath, List<MissingScriptHit> hits)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                ScanHierarchy(root, prefabPath, "Prefab", hits);
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static void ScanHierarchy(GameObject root, string assetPath, string assetKind, List<MissingScriptHit> hits)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var count = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                if (count <= 0)
                    continue;

                hits.Add(
                    new()
                    {
                        AssetPath = assetPath,
                        AssetKind = assetKind,
                        ObjectPath = ConduitUtility.BuildHierarchyPath(transform),
                        MissingScriptCount = count,
                        NearestPrefabAssetPath = PrefabUtility.IsPartOfPrefabInstance(transform.gameObject)
                            ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(transform.gameObject)
                            : null,
                    }
                );
            }
        }

        struct MissingScriptHit
        {
            public string AssetPath;
            public string AssetKind;
            public string ObjectPath;
            public int MissingScriptCount;
            public string? NearestPrefabAssetPath;
        }
    }
}
