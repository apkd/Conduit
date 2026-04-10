#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.Search;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit
{
    enum ResolvedObjectMatchSource
    {
#if UNITY_6000_2_OR_NEWER
        EntityId,
#else
        InstanceId,
#endif
        EditorWindowQuery,
        AssetPath,
        HierarchyPath,
        SearchQuery,
    }

    static class ConduitSearchUtility
    {
        const int MaxResults = 25;
        static readonly string[] SearchProviderIds = { "asset", "scene" };

        public static List<ResolvedObjectMatch> Resolve(string query, int maxResults = MaxResults)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedQuery))
                return new();

            if (IsEditorWindowQuery(normalizedQuery))
                return ResolveEditorWindowQuery(normalizedQuery, maxResults);

            if (TryResolveObjectId(normalizedQuery, out var objectIdMatch, out var isObjectIdQuery))
                return new() { objectIdMatch };

            if (isObjectIdQuery)
                return new();

            if (TryResolveAssetPath(normalizedQuery, out var assetMatch))
                return new() { assetMatch };

            var hierarchyMatches = ResolveHierarchyPath(normalizedQuery);
            if (hierarchyMatches.Count > 0)
                return hierarchyMatches;

            return SearchByQuery(normalizedQuery, maxResults);
        }

        public static string Search(string query)
        {
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var matches = Resolve(normalizedQuery, MaxResults);
            return matches.Count == 0
                ? $"No matches for '{normalizedQuery}'."
                : FormatMatches(matches, includeHint: false);
        }

        public static string FormatMatches(IReadOnlyList<ResolvedObjectMatch> matches, bool includeHint)
        {
            if (matches.Count > 0 && AreEditorWindowMatches(matches))
                return FormatEditorWindowMatches(matches, includeHint);

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            foreach (var match in matches)
                builder.AppendLine($"- {match.Name} | {match.Location} | {ConduitUtility.FormatObjectId(match.ObjectId)}");

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

            return builder.TrimEnd().ToString();
        }

        static bool TryResolveObjectId(string query, out ResolvedObjectMatch match, out bool isObjectIdQuery)
        {
            isObjectIdQuery = false;
#if UNITY_6000_2_OR_NEWER
            return TryResolveEntityId(query, out match, out isObjectIdQuery);
#else
            return TryResolveInstanceId(query, out match, out isObjectIdQuery);
#endif
        }

#if UNITY_6000_2_OR_NEWER
        static bool TryResolveEntityId(string query, out ResolvedObjectMatch match, out bool isObjectIdQuery)
        {
            match = default;
            isObjectIdQuery = TryGetObjectIdValue(query, out var candidate);
            if (!isObjectIdQuery
                || candidate.IsEmpty
#if UNITY_6000_4_OR_NEWER
                || !ulong.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawEntityId))
#else
                || !int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawEntityId))
#endif
                return false;

#if UNITY_6000_4_OR_NEWER
            var entityId = EntityId.FromULong(rawEntityId);
#else
            var entityId = (EntityId)rawEntityId;
#endif
            if (!entityId.IsValid())
                return false;

            var target = EditorUtility.EntityIdToObject(entityId);
            if (target == null)
                return false;

            match = CreateMatch(target, ResolvedObjectMatchSource.EntityId);
            return true;
        }
#else
        static bool TryResolveInstanceId(string query, out ResolvedObjectMatch match, out bool isObjectIdQuery)
        {
            match = default;
            isObjectIdQuery = TryGetObjectIdValue(query, out var candidate);
            if (!isObjectIdQuery
                || candidate.IsEmpty
                || !int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var instanceId))
                return false;

            var target = EditorUtility.InstanceIDToObject(instanceId);
            if (target == null)
                return false;

            match = CreateMatch(target, ResolvedObjectMatchSource.InstanceId);
            return true;
        }
#endif

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

        static List<ResolvedObjectMatch> ResolveHierarchyPath(string query)
        {
            if (!LooksLikeHierarchyPath(query))
                return new();

            var normalizedPath = NormalizeHierarchyPath(query);
            var matches = new List<ResolvedObjectMatch>();
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                    continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (ConduitUtility.BuildHierarchyPath(transform) != normalizedPath)
                            continue;

                        matches.Add(CreateMatch(transform.gameObject, ResolvedObjectMatchSource.HierarchyPath));
                    }
                }
            }

            return Deduplicate(matches, MaxResults);
        }

        static List<ResolvedObjectMatch> SearchByQuery(string query, int maxResults)
        {
            using var context = SearchService.CreateContext(
                SearchProviderIds,
                query,
                SearchFlags.Synchronous
            );

            var items = SearchService.GetItems(
                context,
                SearchFlags.Synchronous
            );

            var matches = new List<ResolvedObjectMatch>();

            foreach (var item in items)
            {
                var target = item.ToObject();
                if (target == null)
                    continue;

                matches.Add(CreateMatch(target, ResolvedObjectMatchSource.SearchQuery));
            }

            return Deduplicate(matches, maxResults);
        }

        static bool IsEditorWindowQuery(string query)
            => query.StartsWith("window:", StringComparison.OrdinalIgnoreCase);

        static List<ResolvedObjectMatch> FindOpenEditorWindowMatches(string query, int maxResults)
        {
            var matches = new List<ResolvedObjectMatch>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || !MatchesEditorWindowQuery(window, query))
                    continue;

                matches.Add(CreateMatch(window, ResolvedObjectMatchSource.EditorWindowQuery));
                if (matches.Count >= maxResults)
                    break;
            }

            matches.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return Deduplicate(matches, maxResults);
        }

        static List<ResolvedObjectMatch> FindEditorWindowTypeMatches(string query, int maxResults)
        {
            var matches = new List<ResolvedObjectMatch>();
            foreach (var windowType in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (!windowType.IsClass || windowType.IsAbstract || windowType.ContainsGenericParameters)
                    continue;

                if (!ContainsIgnoreCase(windowType.Name, query))
                    continue;

                matches.Add(
                    new()
                    {
                        Name = windowType.Name,
                        Location = "EditorWindow type",
                        Source = ResolvedObjectMatchSource.EditorWindowQuery,
                    }
                );

                if (matches.Count >= maxResults)
                    break;
            }

            matches.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            return matches;
        }

        static List<ResolvedObjectMatch> OpenEditorWindowTypeMatch(ResolvedObjectMatch typeMatch)
        {
            Type? windowType = null;
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<EditorWindow>())
            {
                if (!string.Equals(candidate.Name, typeMatch.Name, StringComparison.Ordinal))
                    continue;

                windowType = candidate;
                break;
            }

            if (windowType == null)
                return new();

            try
            {
                var window = EditorWindow.GetWindow(windowType, false, null, true);
                return window == null
                    ? new()
                    : new() { CreateMatch(window, ResolvedObjectMatchSource.EditorWindowQuery) };
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"Could not open editor window type '{windowType.Name}': {exception.Message}");
            }
        }

        internal static string GetEditorWindowDisplayName(EditorWindow window)
        {
            var title = GetEditorWindowTitle(window);
            return string.IsNullOrWhiteSpace(title) ? window.GetType().Name : title;
        }

        internal static string GetEditorWindowTitle(EditorWindow window)
            => window.titleContent?.text?.Trim() ?? string.Empty;

        static bool MatchesEditorWindowQuery(EditorWindow window, string query)
            => ContainsIgnoreCase(GetEditorWindowTitle(window), query)
               || ContainsIgnoreCase(window.GetType().Name, query);

        static bool ContainsIgnoreCase(string value, string query)
            => !string.IsNullOrEmpty(value)
               && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool AreEditorWindowMatches(IReadOnlyList<ResolvedObjectMatch> matches)
        {
            foreach (var match in matches)
                if (match.Source != ResolvedObjectMatchSource.EditorWindowQuery)
                    return false;

            return true;
        }

        static string FormatEditorWindowMatches(IReadOnlyList<ResolvedObjectMatch> matches, bool includeHint)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            foreach (var match in matches)
                builder.AppendLine(
                    match.ObjectId != 0
                        ? $"- {match.Name} | {match.Location} | {ConduitUtility.FormatObjectId(match.ObjectId)}"
                        : $"- {match.Name} | {match.Location}"
                );

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple editor windows match your query.");
                builder.Append("Rerun with a more specific window title or type name.");
            }

            return builder.TrimEnd().ToString();
        }

        static List<ResolvedObjectMatch> Deduplicate(IEnumerable<ResolvedObjectMatch> matches, int maxResults)
        {
            var uniqueMatches = new Dictionary<ulong, ResolvedObjectMatch>();
            foreach (var match in matches)
            {
                if (uniqueMatches.ContainsKey(match.ObjectId))
                    continue;

                uniqueMatches.Add(match.ObjectId, match);
                if (uniqueMatches.Count >= maxResults)
                    break;
            }

            var ordered = new List<ResolvedObjectMatch>(uniqueMatches.Count);
            foreach (var match in uniqueMatches.Values)
                ordered.Add(match);

            ordered.Sort(static (left, right)
                    =>
                {
                    var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                    return nameComparison != 0
                        ? nameComparison
                        : StringComparer.OrdinalIgnoreCase.Compare(left.Location, right.Location);
                }
            );

            return ordered;
        }

        static ResolvedObjectMatch CreateMatch(Object target, ResolvedObjectMatchSource source)
            => new()
            {
                Target = target,
                Name = target is EditorWindow window
                    ? GetEditorWindowDisplayName(window)
                    : target.name,
                Location = GetLocation(target),
                ObjectId = ConduitUtility.GetObjectId(target),
                Source = source,
            };

        static string GetLocation(Object target)
        {
            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(assetPath))
                return assetPath;

            return target switch
            {
                EditorWindow window => $"EditorWindow:{GetEditorWindowDisplayName(window)} ({window.GetType().Name})",
                GameObject gameObject => FormatSceneLocation(gameObject.scene, ConduitUtility.BuildHierarchyPath(gameObject.transform)),
                Component component   => FormatSceneLocation(component.gameObject.scene, ConduitUtility.BuildHierarchyPath(component.transform)),
                _                     => target.GetType().Name,
            };
        }

        static string FormatSceneLocation(Scene scene, string hierarchyPath)
            => $"{ConduitUtility.FormatScenePath(scene, "unsaved scene")}:{hierarchyPath}";

        static bool LooksLikeHierarchyPath(string query)
            => query.StartsWith("/", StringComparison.Ordinal)
               || query.IndexOf('/') >= 0
               || query.IndexOf('\\') >= 0;

        static string NormalizeHierarchyPath(string query)
        {
            var trimmed = query.AsSpan().Trim();
            while (!trimmed.IsEmpty && trimmed[0] == '/')
                trimmed = trimmed[1..];

            return NormalizeSlashes(trimmed);
        }

        static string NormalizeSlashes(ReadOnlySpan<char> value)
        {
            var hasBackslashes = false;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] != '\\')
                    continue;

                hasBackslashes = true;
                break;
            }

            if (!hasBackslashes)
                return value.ToString();

            var buffer = value.ToArray();
            for (var index = 0; index < buffer.Length; index++)
                if (buffer[index] == '\\')
                    buffer[index] = '/';

            return new(buffer);
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

    struct ResolvedObjectMatch
    {
        public Object Target;
        public string Name;
        public string Location;
        public ulong ObjectId;
        public ResolvedObjectMatchSource Source;
    }
}
