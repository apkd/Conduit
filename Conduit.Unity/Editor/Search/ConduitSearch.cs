#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Provides universal Unity object lookup helpers for generated execute_code snippets.</summary>
    public static class ConduitSearch
    {
        /// <summary>Finds exactly one Unity object using the same query syntax as the Conduit search tools.</summary>
        /// <param name="query">An object ID, asset path, hierarchy path, editor-window query, or Unity Search query.</param>
        /// <returns>The single resolved Unity object.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the query resolves to zero or multiple objects.</exception>
        public static Object Search(string query)
            => SelectSingle(query, SearchMany(query, includeAllSearchResults: false));

        /// <summary>Finds every Unity object matching the same query syntax as the Conduit search tools.</summary>
        /// <param name="query">An object ID, asset path, hierarchy path, editor-window query, or Unity Search query.</param>
        /// <returns>Every resolved Unity object, or an empty array when no object matches.</returns>
        public static Object[] SearchMany(string query)
            => SearchMany(query, includeAllSearchResults: true);

        /// <inheritdoc cref="Search(string)" />
        /// <remarks>
        /// General Unity Search queries are intersected with a <c>t:</c> filter for <typeparamref name="T"/>.
        /// Exact IDs, asset paths, hierarchy paths, and editor-window queries are resolved first, then filtered in memory.
        /// Component type parameters return matching components; GameObject matches expand to all assignable components.
        /// </remarks>
        public static T Search<T>(string query) where T : Object
        {
            var matches = SearchMany<T>(query, includeAllSearchResults: false, out var resolvedQuery);
            return SelectSingle(resolvedQuery, matches);
        }

        /// <inheritdoc cref="SearchMany(string)" />
        /// <remarks>
        /// General Unity Search queries are intersected with a <c>t:</c> filter for <typeparamref name="T"/>.
        /// Exact IDs, asset paths, hierarchy paths, and editor-window queries are resolved first, then filtered in memory.
        /// Component type parameters return matching components; GameObject matches expand to all assignable components.
        /// </remarks>
        public static T[] SearchMany<T>(string query) where T : Object
            => SearchMany<T>(query, includeAllSearchResults: true, out _);

        static Object[] SearchMany(string query, bool includeAllSearchResults)
        {
            // singular search keeps the normal tool cap so ambiguity messages stay compact.
            // SearchMany opts into the wider resolver because callers explicitly asked for an array.
            var matches = includeAllSearchResults
                ? ConduitSearchUtility.ResolveAll(query)
                : ConduitSearchUtility.Resolve(query);

            if (matches.Count == 0)
                return Array.Empty<Object>();

            using var pooledResults = ConduitPool.GetPooledList<Object>(out var results);
            foreach (var match in matches)
                if (match.Target != null)
                    results.Add(match.Target);

            return results.Count == 0 ? Array.Empty<Object>() : results.ToArray();
        }

        static T[] SearchMany<T>(
            string query,
            bool includeAllSearchResults,
            out string resolvedQuery) where T : Object
        {
            var requestedType = typeof(T);
            var normalizedQuery = query?.Trim() ?? string.Empty;
            // exact selectors are resolved before adding type filters so paths, IDs, and hierarchy queries
            // keep their precise lookup semantics and can still be adapted to components in memory.
            List<ResolvedObjectMatch> matches;
            if (normalizedQuery.Length > 0
                && ConduitSearchUtility.TryResolveDirect(
                    normalizedQuery,
                    includeAllSearchResults ? int.MaxValue : 25,
                    out var directMatches
                ))
                matches = directMatches;
            else
                matches = ResolveTypedSearchQuery(
                    normalizedQuery,
                    requestedType,
                    includeAllSearchResults,
                    out normalizedQuery
                );

            resolvedQuery = normalizedQuery;
            return FilterMatches<T>(matches, requestedType);
        }

        static List<ResolvedObjectMatch> ResolveTypedSearchQuery(
            string query,
            Type requestedType,
            bool includeAllSearchResults,
            out string resolvedQuery)
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

        static T[] FilterMatches<T>(
            IReadOnlyList<ResolvedObjectMatch> matches,
            Type requestedType) where T : Object
        {
            if (matches.Count == 0)
                return Array.Empty<T>();

            using var pooledResults = ConduitPool.GetPooledList<T>(out var results);
            using var pooledSeen = ConduitPool.GetPooledSet<ulong>(out var seen);
            using var pooledComponents = ConduitPool.GetPooledList<Component>(out var components);
            foreach (var match in matches)
                CollectTypedTargets(match.Target, requestedType, results, seen, components);

            return results.Count == 0 ? Array.Empty<T>() : results.ToArray();
        }

        static void CollectTypedTargets<T>(
            Object? target,
            Type requestedType,
            List<T> results,
            HashSet<ulong> seen,
            List<Component> components) where T : Object
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

            if (target is not GameObject gameObject
                || !typeof(Component).IsAssignableFrom(requestedType))
                return;

            // Unity Search often reports the owning GameObject for component queries.
            // expanding here lets snippets ask for the component type they actually need.
            components.Clear();
            gameObject.GetComponents(requestedType, components);
            foreach (var component in components)
                if (component is T typedComponent)
                    AddUnique(typedComponent, results, seen);
        }

        static void AddUnique<T>(T target, List<T> results, HashSet<ulong> seen) where T : Object
        {
            // object IDs deduplicate paths that can report both an owner and a component target.
            if (target == null || !seen.Add(ConduitObjectId.GetObjectId(target)))
                return;

            results.Add(target);
        }

        static T SelectSingle<T>(string query, IReadOnlyList<T> targets) where T : Object
        {
            if (targets.Count == 1)
                return targets[0];

            if (targets.Count == 0)
                throw new InvalidOperationException(ConduitSearchUtility.FormatNoMatches(query));

            using var pooledObjects = ConduitPool.GetPooledList<Object>(out var objects);
            foreach (var target in targets)
                if (target != null)
                    objects.Add(target);

            // reuse tool-style formatting so generated snippets get actionable IDs for disambiguation.
            throw new InvalidOperationException(
                ConduitSearchUtility.FormatObjects(objects, includeHint: true)
            );
        }
    }
}
