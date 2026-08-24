#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ConduitSearchUtility
    {
        const int MaxResults = 25;
        static readonly string[] SearchProviderIds = { "asset", "scene" };
        internal static List<ResolvedObjectMatch> Resolve(string query, int maxResults = MaxResults + 1)
            => Resolve(query, maxResults, includeAllSearchResults: false);

        internal static List<ResolvedObjectMatch> ResolveAll(string query)
            => Resolve(query, int.MaxValue, includeAllSearchResults: true);

        static List<ResolvedObjectMatch> Resolve(string query, int maxResults, bool includeAllSearchResults)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (normalizedQuery.Length == 0)
                return new();

            if (TryResolveDirect(normalizedQuery, maxResults, out var directMatches))
                return directMatches;

            return SearchByQuery(normalizedQuery, maxResults, includeAllSearchResults);
        }

        internal static bool TryResolveDirect(string query, int maxResults, out List<ResolvedObjectMatch> matches)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            matches = null!;
            if (normalizedQuery.Length == 0)
                return false;

            // direct selectors bypass Unity Search query parsing, so exact paths and IDs keep stable meaning.
            // ConduitSearch uses this path before adding generic type filters.
            if (IsEditorWindowQuery(normalizedQuery))
            {
                matches = ResolveEditorWindowQuery(normalizedQuery, maxResults);
                return true;
            }

            if (TryResolveObjectId(normalizedQuery, out var objectIdMatch, out var isObjectIdQuery))
            {
                matches = new() { objectIdMatch };
                return true;
            }

            if (isObjectIdQuery)
            {
                matches = new();
                return true;
            }

            if (TryResolveAssetPath(normalizedQuery, out var assetMatch))
            {
                matches = new() { assetMatch };
                return true;
            }

            if (LooksLikeHierarchyPath(normalizedQuery))
            {
                var hierarchyMatches = ResolveHierarchyPath(normalizedQuery, maxResults);
                if (hierarchyMatches.Count > 0)
                {
                    matches = hierarchyMatches;
                    return true;
                }
            }

            return false;
        }

        internal static string Search(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (UnityTestSearch.TryParse(normalizedQuery, out var testSearch))
                return UnityTestSearch.Search(normalizedQuery, testSearch);

            var matches = Resolve(normalizedQuery, MaxResults + 1);
            return matches.Count == 0
                ? FormatNoMatches(normalizedQuery)
                : FormatMatches(matches, includeHint: false);
        }

        internal static string[] ResolveAssetPaths(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (normalizedQuery.Length == 0)
                return Array.Empty<string>();

            var matches = Resolve(normalizedQuery, int.MaxValue);
            var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in matches)
                CollectImportableAssetPaths(match, assetPaths);

            return ConduitPatternUtility.SortStrings(assetPaths, StringComparer.OrdinalIgnoreCase);
        }

        internal static string FormatNoMatches(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            return ShouldWarnAboutUnsupportedOrSyntax(normalizedQuery)
                ? "Unity search does not support OR operators. Run separate queries instead."
                : $"No matches for '{normalizedQuery}'.";
        }

        static bool ShouldWarnAboutUnsupportedOrSyntax(string query)
            => !string.IsNullOrWhiteSpace(query)
               && !IsEditorWindowQuery(query)
               && !query.StartsWith("/", StringComparison.Ordinal)
               && !query.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
               && !query.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
               && !TryGetObjectIdValue(query, out _)
               && !UnityTestSearch.TryParse(query, out _)
               && (query.Contains("||", StringComparison.Ordinal)
                   || query.Contains(" OR ", StringComparison.Ordinal));

        internal static string FormatMatches(IReadOnlyList<ResolvedObjectMatch> matches, bool includeHint)
        {
            if (matches.Count > 0 && AreEditorWindowMatches(matches))
                return FormatEditorWindowMatches(matches, includeHint);

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            for (var index = 0; index < Math.Min(matches.Count, MaxResults); index++)
            {
                var match = matches[index];
                builder.Append("- ")
                    .Append(match.Name)
                    .Append(" | ")
                    .Append(match.Location)
                    .Append(" | ")
                    .AppendLine(ConduitObjectId.FormatObjectId(match.ObjectId));
            }

            AppendTruncationNotice(builder, matches.Count);

#if UNITY_6000_2_OR_NEWER
            const string objectIdExample = "eid:<number>";
#else
            const string objectIdExample = "id:<number>";
#endif

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple objects match your query.");
                builder.Append($"Rerun with {objectIdExample} to select a specific match.");
            }

            return builder.ToTrimmedString();
        }

        internal static string FormatObjects(IReadOnlyList<Object> targets, bool includeHint)
        {
            using var pooledMatches = ConduitPool.GetPooledList<ResolvedObjectMatch>(out var matches);
            // expanded component matches need the same candidate format as resolver matches.
            foreach (var target in targets)
                if (target != null)
                    matches.Add(CreateMatch(target, ResolvedObjectMatchSource.SearchQuery));

            return FormatMatches(matches, includeHint);
        }

        static bool TryResolveObjectId(string query, out ResolvedObjectMatch match, out bool isObjectIdQuery)
        {
            match = default;
            isObjectIdQuery = TryGetObjectIdValue(query, out var candidate);
            if (!isObjectIdQuery
                || candidate.IsEmpty
                || !BridgeObjectId.TryParse(candidate, out var objectId))
                return false;

            var target = ConduitObjectId.ResolveObjectId(objectId);
            if (target == null)
                return false;

#if UNITY_6000_2_OR_NEWER
            match = CreateMatch(target, ResolvedObjectMatchSource.EntityId);
#else
            match = CreateMatch(target, ResolvedObjectMatchSource.InstanceId);
#endif
            return true;
        }

        static bool TryResolveAssetPath(string query, out ResolvedObjectMatch match)
        {
            match = default;
            if (!ConduitAssetPathUtility.TryResolveAssetPath(query, out var assetPath))
                return false;

            var target = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (target == null)
                return false;

            match = CreateMatch(target, ResolvedObjectMatchSource.AssetPath);
            return true;
        }

        static List<ResolvedObjectMatch> ResolveEditorWindowQuery(string query, int maxResults)
        {
            var windowQuery = query["window:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(windowQuery))
                return new();

            var openMatches = FindOpenEditorWindowMatches(windowQuery, maxResults);
            if (openMatches.Count > 0)
                return openMatches;

            var typeMatches = FindEditorWindowTypeMatches(windowQuery, maxResults);
            if (typeMatches.Count == 1)
                return OpenEditorWindowTypeMatch(typeMatches[0]);

            return typeMatches;
        }

        static List<ResolvedObjectMatch> ResolveHierarchyPath(string query, int maxResults)
        {
            var normalizedPath = NormalizeHierarchyPath(query);
            var matches = new List<ResolvedObjectMatch>(Math.Min(maxResults, 4));
            using var pooledRoots = ConduitPool.GetPooledList<GameObject>(out var roots);
            using var pooledPending = ConduitPool.GetPooledList<(Transform Transform, int PathOffset)>(out var pending);
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                roots.Clear();
                scene.GetRootGameObjects(roots);
                foreach (var root in roots)
                {
                    pending.Clear();
                    pending.Add((root.transform, 0));
                    while (pending.Count > 0)
                    {
                        var lastIndex = pending.Count - 1;
                        var (transform, pathOffset) = pending[lastIndex];
                        pending.RemoveAt(lastIndex);

                        // compare raw name prefixes because Unity object names may contain path separators.
                        var name = transform.name;
                        if (normalizedPath.Length - pathOffset < name.Length
                            || string.CompareOrdinal(normalizedPath, pathOffset, name, 0, name.Length) != 0)
                            continue;

                        var nextOffset = pathOffset + name.Length;
                        if (nextOffset == normalizedPath.Length)
                        {
                            matches.Add(CreateMatch(transform.gameObject, ResolvedObjectMatchSource.HierarchyPath));
                            if (matches.Count >= maxResults)
                                return Deduplicate(matches, maxResults);
                            continue;
                        }

                        if (normalizedPath[nextOffset] != '/')
                            continue;

                        ++nextOffset;
                        for (var childIndex = transform.childCount - 1; childIndex >= 0; --childIndex)
                            pending.Add((transform.GetChild(childIndex), nextOffset));
                    }

                    if (matches.Count >= maxResults)
                        return Deduplicate(matches, maxResults);
                }
            }

            return Deduplicate(matches, maxResults);
        }

        static List<ResolvedObjectMatch> SearchByQuery(string query, int maxResults, bool includeAllSearchResults)
        {
            // Unity Search applies provider limits by default; array-returning APIs need the complete set.
            var searchQuery = includeAllSearchResults ? AddNoResultsLimit(query) : query;
            using var context = SearchService.CreateContext(
                SearchProviderIds,
                searchQuery,
                SearchFlags.Synchronous
            );

            var items = SearchService.GetItems(
                context,
                SearchFlags.Synchronous
            );

            var matches = new List<ResolvedObjectMatch>(
                maxResults == int.MaxValue ? MaxResults : Math.Min(maxResults, MaxResults + 1)
            );
            using var pooledObjectIds = ConduitPool.GetPooledSet<ulong>(out var objectIds);

            foreach (var item in items)
            {
                var target = item.ToObject();
                if (target == null)
                    continue;

                var match = CreateMatch(target, ResolvedObjectMatchSource.SearchQuery);
                if (!objectIds.Add(match.ObjectId))
                    continue;

                matches.Add(match);
                if (matches.Count >= maxResults)
                    break;
            }

            SortMatches(matches);
            return matches;
        }

        static string AddNoResultsLimit(string query)
        {
            if (query.IndexOf("+noResultsLimit", StringComparison.OrdinalIgnoreCase) >= 0)
                return query;

            // keep user-authored query text intact and append the provider directive as a separate token.
            return string.IsNullOrWhiteSpace(query)
                ? "+noResultsLimit"
                : query + " +noResultsLimit";
        }

    }
}
