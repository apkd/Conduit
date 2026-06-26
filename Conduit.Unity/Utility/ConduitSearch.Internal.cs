#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    public static partial class ConduitSearch
    {
        static Object[] SearchMany(string query, bool includeAllSearchResults)
        {
            // singular search keeps the normal tool cap so ambiguity messages stay compact.
            // SearchMany opts into the wider resolver because callers explicitly asked for an array.
            var matches = includeAllSearchResults
                ? ConduitSearchUtility.ResolveAll(query)
                : ConduitSearchUtility.Resolve(query);

            if (matches.Count == 0)
                return Array.Empty<Object>();

            using var pooledResults = ConduitUtility.GetPooledList<Object>(out var results);
            foreach (var match in matches)
                if (match.Target != null)
                    results.Add(match.Target);

            return results.Count == 0 ? Array.Empty<Object>() : results.ToArray();
        }

        static T[] SearchMany<T>(string query, bool includeAllSearchResults, out string resolvedQuery) where T : Object
        {
            var requestedType = typeof(T);
            var normalizedQuery = query?.Trim() ?? string.Empty;
            var directMatches = new List<ResolvedObjectMatch>();
            // exact selectors are resolved before adding type filters so paths, IDs, and hierarchy queries
            // keep their precise lookup semantics and can still be adapted to components in memory.
            var useDirectMatches = normalizedQuery.Length > 0
                                   && ConduitSearchUtility.TryResolveDirect(
                                       normalizedQuery,
                                       includeAllSearchResults ? int.MaxValue : 25,
                                       out directMatches
                                   );
            var matches = useDirectMatches
                ? directMatches
                : ResolveTypedSearchQuery(normalizedQuery, requestedType, includeAllSearchResults, out normalizedQuery);

            resolvedQuery = normalizedQuery;
            return FilterMatches<T>(matches, requestedType);
        }

        static List<ResolvedObjectMatch> ResolveTypedSearchQuery(
            string query,
            Type requestedType,
            bool includeAllSearchResults,
            out string resolvedQuery
        )
        {
            resolvedQuery = AppendTypeFilter(query, requestedType);
            return includeAllSearchResults
                ? ConduitSearchUtility.ResolveAll(resolvedQuery)
                : ConduitSearchUtility.Resolve(resolvedQuery);
        }

        static string AppendTypeFilter(string query, Type requestedType)
        {
            if (requestedType == typeof(Object))
                return query;

            // generic overloads are an intersection, even when the user supplied another t: token.
            // this keeps Search<Camera>("foo t:Light") aligned with the requested generic type.
            var filter = "t:" + GetSearchTypeName(requestedType);
            return string.IsNullOrWhiteSpace(query) ? filter : query + " " + filter;
        }

        static string GetSearchTypeName(Type type)
        {
            var typeName = type.Name;
            var arityIndex = typeName.IndexOf('`');
            // Unity Search uses display type names rather than CLR generic arity suffixes.
            return arityIndex < 0 ? typeName : typeName[..arityIndex];
        }

        static T[] FilterMatches<T>(IReadOnlyList<ResolvedObjectMatch> matches, Type requestedType) where T : Object
        {
            if (matches.Count == 0)
                return Array.Empty<T>();

            using var pooledResults = ConduitUtility.GetPooledList<T>(out var results);
            using var pooledSeen = ConduitUtility.GetPooledSet<ulong>(out var seen);
            foreach (var match in matches)
                CollectTypedTargets(match.Target, requestedType, results, seen);

            return results.Count == 0 ? Array.Empty<T>() : results.ToArray();
        }

        static void CollectTypedTargets<T>(
            Object target,
            Type requestedType,
            List<T> results,
            HashSet<ulong> seen
        ) where T : Object
        {
            if (target == null)
                return;

            if (requestedType == typeof(Object))
            {
                AddUnique((T)target, results, seen);
                return;
            }

            if (target is T typedTarget)
            {
                AddUnique(typedTarget, results, seen);
                return;
            }

            if (target is not GameObject gameObject || !typeof(Component).IsAssignableFrom(requestedType))
                return;

            // Unity Search often reports the owning GameObject for component queries.
            // expanding here lets snippets ask for the component type they actually need.
            foreach (var component in gameObject.GetComponents(requestedType))
                if (component is T typedComponent)
                    AddUnique(typedComponent, results, seen);
        }

        static void AddUnique<T>(T target, List<T> results, HashSet<ulong> seen) where T : Object
        {
            // object IDs deduplicate paths that can report both an owner and a component target.
            if (target == null || !seen.Add(ConduitUtility.GetObjectId(target)))
                return;

            results.Add(target);
        }

        static T SelectSingle<T>(string query, IReadOnlyList<T> targets) where T : Object
        {
            if (targets.Count == 1)
                return targets[0];

            if (targets.Count == 0)
                throw new InvalidOperationException(ConduitSearchUtility.FormatNoMatches(query));

            using var pooledObjects = ConduitUtility.GetPooledList<Object>(out var objects);
            foreach (var target in targets)
                if (target != null)
                    objects.Add(target);

            // reuse tool-style formatting so generated snippets get actionable IDs for disambiguation.
            throw new InvalidOperationException(ConduitSearchUtility.FormatObjects(objects, includeHint: true));
        }
    }
}
