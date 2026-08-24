#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    /// <summary>Finds loaded player objects for generated execute_code snippets.</summary>
    public static partial class ConduitRuntimeSearch
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
            var results = ListPool<SearchResult>.Get();
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
                    var result = new SearchResult(candidate, name, objectId);
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
                ListPool<SearchResult>.Release(results);
                if (componentObjectIds != null)
                    CollectionPool<HashSet<ulong>, ulong>.Release(componentObjectIds);
            }
        }

    }
}
