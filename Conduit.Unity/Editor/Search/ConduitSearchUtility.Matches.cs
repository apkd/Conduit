#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ConduitSearchUtility
    {
        static void AppendTruncationNotice(StringBuilder builder, int resultCount)
        {
            if (resultCount <= MaxResults)
                return;

            builder.AppendLine();
            builder.AppendLine($"Showing the first {MaxResults} results; additional matches were omitted.");
            builder.AppendLine("More specific queries return a narrower result set.");
        }

        static void CollectImportableAssetPaths(ResolvedObjectMatch match, HashSet<string> assetPaths)
        {
            if (match.AssetPath is not { Length: > 0 } assetPath)
                return;

            CollectImportableAssetPaths(assetPath, assetPaths);
        }

        static void CollectImportableAssetPaths(string assetPath, HashSet<string> assetPaths)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return;

            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                if (IsImportableAssetPath(assetPath))
                    assetPaths.Add(assetPath);

                return;
            }

            var addedChildren = false;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { assetPath }))
            {
                var childAssetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!IsImportableAssetPath(childAssetPath) || AssetDatabase.IsValidFolder(childAssetPath))
                    continue;

                assetPaths.Add(childAssetPath);
                addedChildren = true;
            }

            if (!addedChildren && IsImportableAssetPath(assetPath))
                assetPaths.Add(assetPath);
        }

        static bool IsImportableAssetPath(string assetPath)
            => !string.IsNullOrWhiteSpace(assetPath)
               && !assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
               && (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                   || assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                   || assetPath.Equals("Assets", StringComparison.OrdinalIgnoreCase)
                   || assetPath.Equals("Packages", StringComparison.OrdinalIgnoreCase));

        static List<ResolvedObjectMatch> Deduplicate(List<ResolvedObjectMatch> matches, int maxResults)
        {
            using var pooledObjectIds = ConduitPool.GetPooledSet<ulong>(out var objectIds);
            var uniqueCount = 0;
            for (var readIndex = 0; readIndex < matches.Count; readIndex++)
            {
                var match = matches[readIndex];
                if (!objectIds.Add(match.ObjectId))
                    continue;

                matches[uniqueCount++] = match;
                if (uniqueCount >= maxResults)
                    break;
            }

            if (uniqueCount < matches.Count)
                matches.RemoveRange(uniqueCount, matches.Count - uniqueCount);

            SortMatches(matches);
            return matches;
        }

        static void SortMatches(List<ResolvedObjectMatch> matches)
            => matches.Sort(static (left, right)
                    =>
                {
                    var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                    return nameComparison != 0
                        ? nameComparison
                        : StringComparer.OrdinalIgnoreCase.Compare(left.Location, right.Location);
                }
            );

        static ResolvedObjectMatch CreateMatch(Object target, ResolvedObjectMatchSource source)
        {
            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;
            return new(
                target,
                target is EditorWindow window
                    ? GetEditorWindowDisplayName(window)
                    : target.name,
                GetLocation(target, assetPath),
                assetPath,
                ConduitObjectId.GetObjectId(target),
                source
            );
        }

        static string GetLocation(Object target, string assetPath)
        {
            if (!string.IsNullOrWhiteSpace(assetPath))
                return assetPath;

            return target switch
            {
                EditorWindow window => $"EditorWindow:{GetEditorWindowDisplayName(window)} ({window.GetType().Name})",
                GameObject gameObject => FormatSceneLocation(
                    gameObject.scene,
                    ConduitHierarchyPathUtility.BuildHierarchyPath(gameObject.transform)
                ),
                Component component   => FormatSceneLocation(
                    component.gameObject.scene,
                    ConduitHierarchyPathUtility.BuildHierarchyPath(component.transform)
                ),
                _                     => target.GetType().Name,
            };
        }

        static string FormatSceneLocation(Scene scene, string hierarchyPath)
            => $"{ConduitHierarchyPathUtility.FormatScenePath(scene, "unsaved scene")}:{hierarchyPath}";

        static bool LooksLikeHierarchyPath(string query)
            => query.StartsWith("/", StringComparison.Ordinal)
               || query.IndexOf('/') >= 0
               || query.IndexOf('\\') >= 0;

        static string NormalizeHierarchyPath(string query)
        {
            var start = 0;
            var end = query.Length;
            while (start < end && char.IsWhiteSpace(query[start]))
                start++;
            while (end > start && char.IsWhiteSpace(query[end - 1]))
                end--;
            while (start < end && query[start] == '/')
                start++;

            var hasBackslashes = false;
            for (var index = start; index < end; index++)
            {
                if (query[index] != '\\')
                    continue;

                hasBackslashes = true;
                break;
            }

            if (!hasBackslashes)
                return start == 0 && end == query.Length
                    ? query
                    : query.Substring(start, end - start);

            return string.Create(
                end - start,
                (query, start),
                static (result, state) =>
                {
                    for (var index = 0; index < result.Length; index++)
                    {
                        var character = state.query[state.start + index];
                        result[index] = character == '\\' ? '/' : character;
                    }
                }
            );
        }

        static bool TryGetObjectIdValue(string query, out ReadOnlySpan<char> value)
        {
            var querySpan = query.AsSpan();
            if (TryGetObjectIdValue(querySpan, "eid:", out value)
                || TryGetObjectIdValue(querySpan, "entity:", out value)
                || TryGetObjectIdValue(querySpan, "id:", out value))
                return true;

            value = default;
            return false;
        }

        static bool TryGetObjectIdValue(ReadOnlySpan<char> querySpan, string prefix, out ReadOnlySpan<char> value)
        {
            if (!querySpan.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = default;
                return false;
            }

            value = querySpan[prefix.Length..].TrimStart();
            return true;
        }
    }
}
