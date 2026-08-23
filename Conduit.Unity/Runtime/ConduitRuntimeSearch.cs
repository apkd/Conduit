#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    /// <summary>Finds loaded player objects for generated execute_code snippets.</summary>
    public static class ConduitRuntimeSearch
    {
        const int MaxFormattedResults = 25;
        const int MaxSelectionResults = MaxFormattedResults + 1;

        /// <summary>Finds exactly one loaded Unity object.</summary>
        public static Object Search(string query)
            => SelectSingle<Object>(query, ResolveMany(query, null, MaxSelectionResults));

        /// <summary>Finds loaded Unity objects.</summary>
        public static Object[] SearchMany(string query)
            => ResolveMany(query, null, int.MaxValue).ToArray();

        /// <summary>Finds exactly one loaded Unity object assignable to <typeparamref name="T"/>.</summary>
        public static T Search<T>(string query) where T : Object
            => SelectSingle<T>(
                query,
                Filter<T>(ResolveMany(query, typeof(T), MaxSelectionResults))
            );

        /// <summary>Finds loaded Unity objects assignable to <typeparamref name="T"/>.</summary>
        public static T[] SearchMany<T>(string query) where T : Object
            => Filter<T>(ResolveMany(query, typeof(T), int.MaxValue)).ToArray();

        internal static List<Object> ResolveMany(string? query)
            => ResolveMany(query, null, int.MaxValue);

        internal static List<Object> ResolveManyForDisplay(string? query)
            => ResolveMany(query, null, MaxSelectionResults);

        static List<Object> ResolveMany(string? query, Type? defaultType, int maxResults)
        {
            var normalized = query?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
                return new();

            if (TryResolveObjectId(normalized, out var byId))
                return byId == null ? new() : new() { byId };

            if (normalized[0] == '/')
                return ResolveHierarchyPath(normalized, maxResults);

            ParseQuery(
                normalized,
                out var requestedType,
                out var nameQuery,
                out var unresolvedType
            );
            if (unresolvedType)
                return new();
            var narrowedByGenericType = requestedType == null
                                        && defaultType != null
                                        && defaultType != typeof(Object);
            if (narrowedByGenericType)
                requestedType = defaultType;

            var candidates = requestedType != null && typeof(Object).IsAssignableFrom(requestedType)
                ? Resources.FindObjectsOfTypeAll(requestedType)
                : Resources.FindObjectsOfTypeAll<Object>();
            // generic helpers return the requested components directly; the tool-facing search still reports their owners.
            var componentObjectIds = !narrowedByGenericType
                                     && requestedType != null
                                     && typeof(Component).IsAssignableFrom(requestedType)
                ? CollectionPool<HashSet<ulong>, ulong>.Get()
                : null;
            var bounded = maxResults != int.MaxValue;
            // selective name searches should not reserve one result row for every loaded Unity object.
            var initialCapacity = bounded
                ? maxResults
                : nameQuery.Length == 0
                    ? candidates.Length
                    : Math.Min(candidates.Length, 256);
            var results = ListPool<RuntimeSearchResult>.Get();
            try
            {
                if (results.Capacity < initialCapacity)
                    results.Capacity = initialCapacity;
                foreach (var rawCandidate in candidates)
                {
                    var candidate = componentObjectIds != null && rawCandidate is Component component
                        ? component.gameObject
                        : rawCandidate;
                    if (candidate == null || !IsInspectable(candidate))
                        continue;

                    var objectId = 0UL;
                    if (componentObjectIds != null)
                    {
                        objectId = BridgeObjectId.Get(candidate);
                        if (!componentObjectIds.Add(objectId))
                            continue;
                    }

                    if (requestedType != null
                        && componentObjectIds == null
                        && !MatchesType(candidate, requestedType))
                        continue;

                    var name = candidate.name;
                    if (nameQuery.Length > 0
                        && name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) < 0
                        && candidate.GetType().Name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (componentObjectIds == null)
                        objectId = BridgeObjectId.Get(candidate);
                    var result = new RuntimeSearchResult(candidate, name, objectId);
                    if (!bounded || results.Count < maxResults)
                    {
                        results.Add(result);
                        if (bounded && results.Count == maxResults)
                            BuildMaxHeap(results);
                    }
                    else if (CompareResults(result, results[0]) < 0)
                    {
                        results[0] = result;
                        SiftDown(results, 0);
                    }
                }

                results.Sort(CompareResults);

                var objects = new List<Object>(results.Count);
                foreach (var result in results)
                    objects.Add(result.Target);
                return objects;
            }
            finally
            {
                ListPool<RuntimeSearchResult>.Release(results);
                if (componentObjectIds != null)
                    CollectionPool<HashSet<ulong>, ulong>.Release(componentObjectIds);
            }
        }

        internal static Object ResolveOne(string query)
            => SelectSingle<Object>(query, ResolveMany(query, null, MaxSelectionResults));

        internal static string FormatMatches(IReadOnlyList<Object> matches, bool includeHint)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            for (var index = 0; index < Math.Min(matches.Count, MaxFormattedResults); index++)
            {
                var match = matches[index];
                builder.Append("- ")
                    .Append(match.name)
                    .Append(" (")
                    .Append(match.GetType().Name)
                    .Append(") | ");
                AppendLocation(builder, match);
                builder.Append(" | ")
                    .Append(BridgeObjectId.Format(match))
                    .AppendLine();
            }

            if (matches.Count > MaxFormattedResults)
            {
                builder.AppendLine();
                builder.Append("Showing the first ")
                    .Append(MaxFormattedResults)
                    .AppendLine(" results; additional matches were omitted.");
                builder.AppendLine("More specific queries return a narrower result set.");
            }

            if (includeHint && matches.Count > 1)
            {
                builder.AppendLine();
                builder.AppendLine("Multiple objects match your query.");
                builder.Append("Rerun with ")
                    .Append(BridgeObjectId.Prefix)
                    .AppendLine("<number> to select a specific match.");
            }

            while (builder.Length > 0 && builder[^1] is '\r' or '\n')
                --builder.Length;
            return builder.ToString();
        }

        internal static string GetHierarchyPath(GameObject gameObject)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append('/');
            AppendHierarchyPath(builder, gameObject.transform, 0);
            return builder.ToString();
        }

        static List<T> Filter<T>(IReadOnlyList<Object> objects) where T : Object
        {
            var results = new List<T>(objects.Count);
            List<Component>? components = null;
            var includesComponents = typeof(Component).IsAssignableFrom(typeof(T));
            try
            {
                foreach (var candidate in objects)
                {
                    if (candidate is T typed)
                    {
                        results.Add(typed);
                        continue;
                    }

                    if (candidate is not GameObject gameObject || !includesComponents)
                        continue;

                    components ??= ListPool<Component>.Get();
                    gameObject.GetComponents(typeof(T), components);
                    foreach (var component in components)
                        if (component is T typedComponent)
                            results.Add(typedComponent);
                    components.Clear();
                }
            }
            finally
            {
                if (components != null)
                    ListPool<Component>.Release(components);
            }

            return results;
        }

        static T SelectSingle<T>(string query, IReadOnlyList<T> values) where T : Object
        {
            if (values.Count == 0)
                throw new InvalidOperationException($"No matches for '{query}'.");

            if (values.Count == 1)
                return values[0];

            var count = Math.Min(values.Count, MaxFormattedResults + 1);
            using var pooledMatches = ListPool<Object>.Get(out var matches);
            matches.Clear();
            if (matches.Capacity < count)
                matches.Capacity = count;
            for (var index = 0; index < count; index++)
                matches.Add(values[index]);

            throw new InvalidOperationException(FormatMatches(matches, includeHint: true));
        }

        static bool TryResolveObjectId(string query, out Object? value)
        {
            value = null;
            var prefixLength = query.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                || query.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                ? query.IndexOf(':') + 1
                : 0;
            if (prefixLength == 0
                || !BridgeObjectId.TryParse(query.AsSpan(prefixLength), out var objectId))
                return false;

            value = BridgeObjectId.Resolve(objectId);
            if (value != null && !IsSupportedType(value))
                value = null;
            return true;
        }

        static List<Object> ResolveHierarchyPath(string query, int maxResults)
        {
            var path = query.AsSpan();
            var firstOffset = 0;
            if (!TryReadPathSegment(path, ref firstOffset, out var rootName))
                return new();

            var matches = new List<Object>();
            using var pooledRoots = ListPool<GameObject>.Get(out var roots);
            roots.Clear();
            var sceneCount = SceneManager.sceneCount;
            for (var sceneIndex = 0; sceneIndex < sceneCount; ++sceneIndex)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                roots.Clear();
                scene.GetRootGameObjects(roots);
                foreach (var root in roots)
                {
                    if (!rootName.Equals(root.name.AsSpan(), StringComparison.Ordinal))
                        continue;

                    var current = root.transform;
                    var pathOffset = firstOffset;
                    while (TryReadPathSegment(path, ref pathOffset, out var childName))
                    {
                        current = FindChild(current, childName);
                        if (current == null)
                            break;
                    }

                    if (current != null)
                    {
                        matches.Add(current.gameObject);
                        if (matches.Count == maxResults)
                            return matches;
                    }
                }
            }

            return matches;
        }

        static int CompareResults(RuntimeSearchResult left, RuntimeSearchResult right)
        {
            var name = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            return name != 0
                ? name
                : left.ObjectId.CompareTo(right.ObjectId);
        }

        static void BuildMaxHeap(List<RuntimeSearchResult> results)
        {
            for (var index = results.Count / 2 - 1; index >= 0; --index)
                SiftDown(results, index);
        }

        static void SiftDown(List<RuntimeSearchResult> results, int index)
        {
            while (true)
            {
                var left = index * 2 + 1;
                if (left >= results.Count)
                    return;

                var right = left + 1;
                var worse = right < results.Count
                            && CompareResults(results[right], results[left]) > 0
                    ? right
                    : left;
                if (CompareResults(results[worse], results[index]) <= 0)
                    return;

                (results[index], results[worse]) = (results[worse], results[index]);
                index = worse;
            }
        }

        static bool TryReadPathSegment(
            ReadOnlySpan<char> path,
            ref int offset,
            out ReadOnlySpan<char> segment)
        {
            while (offset < path.Length && path[offset] == '/')
                ++offset;

            if (offset >= path.Length)
            {
                segment = default;
                return false;
            }

            var start = offset;
            while (offset < path.Length && path[offset] != '/')
                ++offset;

            segment = path[start..offset];
            return true;
        }

        static Transform? FindChild(Transform parent, ReadOnlySpan<char> name)
        {
            var childCount = parent.childCount;
            for (var index = 0; index < childCount; ++index)
            {
                var child = parent.GetChild(index);
                if (name.Equals(child.name.AsSpan(), StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        static void ParseQuery(
            string query,
            out Type? requestedType,
            out string nameQuery,
            out bool unresolvedType)
        {
            requestedType = null;
            unresolvedType = false;
            if (query.IndexOf(' ') < 0
                && !query.StartsWith("t:", StringComparison.OrdinalIgnoreCase)
                && !query.StartsWith("t=", StringComparison.OrdinalIgnoreCase))
            {
                nameQuery = query[0] == '+' ? query[1..] : query;
                return;
            }

            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            var partCount = 0;
            var offset = 0;
            while (offset < query.Length)
            {
                while (offset < query.Length && query[offset] == ' ')
                    ++offset;
                if (offset == query.Length)
                    break;

                var start = offset;
                while (offset < query.Length && query[offset] != ' ')
                    ++offset;
                var length = offset - start;
                var part = query.AsSpan(start, length);
                if ((part.StartsWith("t:".AsSpan(), StringComparison.OrdinalIgnoreCase)
                     || part.StartsWith("t=".AsSpan(), StringComparison.OrdinalIgnoreCase))
                    && length > 2)
                {
                    requestedType = ConduitReflect.ResolveType(query.Substring(start + 2, length - 2));
                    unresolvedType |= requestedType == null;
                    continue;
                }

                while (length > 0 && query[start] == '+')
                {
                    ++start;
                    --length;
                }

                if (partCount++ > 0)
                    builder.Append(' ');
                builder.Append(query, start, length);
            }

            nameQuery = builder.ToString();
        }

        static bool MatchesType(Object candidate, Type requestedType)
        {
            if (requestedType.IsInstanceOfType(candidate))
                return true;

            return candidate is GameObject gameObject
                   && typeof(Component).IsAssignableFrom(requestedType)
                   && gameObject.GetComponent(requestedType) != null;
        }

        static bool IsInspectable(Object candidate) =>
            (candidate.hideFlags & HideFlags.HideAndDontSave) == 0 && IsSupportedType(candidate);

        static bool IsSupportedType(Object candidate) =>
            candidate is GameObject
            or Component
            or ScriptableObject
            or Texture
            or Material
            or Mesh
#if MODULE_ANIMATION
            or AnimationClip
            or RuntimeAnimatorController
#endif
            ;

        static void AppendLocation(StringBuilder builder, Object target)
        {
            if (target is GameObject gameObject)
            {
                AppendSceneLocation(builder, gameObject.scene, gameObject.transform);
                return;
            }

            if (target is Component component)
            {
                AppendSceneLocation(builder, component.gameObject.scene, component.transform);
                return;
            }

            builder.Append(target.GetType().Name);
        }

        static void AppendSceneLocation(StringBuilder builder, Scene scene, Transform transform)
        {
            var path = scene.path;
            if (!string.IsNullOrWhiteSpace(path))
                builder.Append(path);
            else
            {
                var name = scene.name;
                if (string.IsNullOrWhiteSpace(name))
                    builder.Append("<unsaved scene>");
                else
                    builder.Append("<unsaved scene:").Append(name).Append('>');
            }

            builder.Append(":/");
            AppendHierarchyPath(builder, transform, 0);
        }

        static void AppendHierarchyPath(StringBuilder builder, Transform transform, int depth)
        {
            if (depth == 256)
            {
                using var pooledAncestors = ListPool<Transform>.Get(out var ancestors);
                ancestors.Clear();
                for (var current = transform; current != null; current = current.parent)
                    ancestors.Add(current);

                for (var index = ancestors.Count - 1; index >= 0; --index)
                {
                    if (index < ancestors.Count - 1)
                        builder.Append('/');

                    builder.Append(ancestors[index].name);
                }
                return;
            }

            var parent = transform.parent;
            if (parent != null)
            {
                AppendHierarchyPath(builder, parent, depth + 1);
                builder.Append('/');
            }

            builder.Append(transform.name);
        }
    }

    readonly struct RuntimeSearchResult
    {
        public readonly Object Target;
        public readonly string Name;
        public readonly ulong ObjectId;

        public RuntimeSearchResult(Object target, string name, ulong objectId)
        {
            Target = target;
            Name = name;
            ObjectId = objectId;
        }
    }
}
