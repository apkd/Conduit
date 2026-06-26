#nullable enable

using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    /// <summary>Provides universal Unity object lookup helpers for generated execute_code snippets.</summary>
    public static partial class ConduitSearch
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
    }
}
