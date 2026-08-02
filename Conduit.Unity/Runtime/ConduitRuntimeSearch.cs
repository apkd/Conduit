#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    /// <summary>Finds loaded player objects for generated execute_code snippets.</summary>
    public static class ConduitRuntimeSearch
    {
        const int MaxFormattedResults = 25;

        /// <summary>Finds exactly one loaded Unity object.</summary>
        public static Object Search(string query)
            => SelectSingle<Object>(query, ResolveMany(query));

        /// <summary>Finds loaded Unity objects.</summary>
        public static Object[] SearchMany(string query)
            => ResolveMany(query).ToArray();

        /// <summary>Finds exactly one loaded Unity object assignable to <typeparamref name="T"/>.</summary>
        public static T Search<T>(string query) where T : Object
            => SelectSingle<T>(query, Filter<T>(ResolveMany(query)));

        /// <summary>Finds loaded Unity objects assignable to <typeparamref name="T"/>.</summary>
        public static T[] SearchMany<T>(string query) where T : Object
            => Filter<T>(ResolveMany(query)).ToArray();

        internal static List<Object> ResolveMany(string? query)
        {
            var normalized = query?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
                return new();

            if (TryResolveObjectId(normalized, out var byId))
                return byId == null ? new() : new() { byId };

            if (normalized[0] == '/')
                return ResolveHierarchyPath(normalized).Cast<Object>().ToList();

            ParseQuery(
                normalized,
                out var requestedType,
                out var nameQuery,
                out var unresolvedType
            );
            if (unresolvedType)
                return new();
            var results = new List<Object>();
            foreach (var candidate in Resources.FindObjectsOfTypeAll<Object>())
            {
                if (candidate == null || !IsInspectable(candidate))
                    continue;

                if (requestedType != null
                    && typeof(Component).IsAssignableFrom(requestedType)
                    && candidate is not GameObject)
                    continue;

                if (requestedType != null && !MatchesType(candidate, requestedType))
                    continue;

                if (nameQuery.Length > 0
                    && candidate.name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) < 0
                    && candidate.GetType().Name.IndexOf(nameQuery, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                results.Add(candidate);
            }

            results.Sort(static (left, right) =>
            {
                var name = string.Compare(left.name, right.name, StringComparison.OrdinalIgnoreCase);
                return name != 0
                    ? name
                    : BridgeObjectId.Get(left).CompareTo(BridgeObjectId.Get(right));
            });
            return results;
        }

        internal static Object ResolveOne(string query)
            => SelectSingle<Object>(query, ResolveMany(query));

        internal static string FormatMatches(IReadOnlyList<Object> matches, bool includeHint)
        {
            var lines = new List<string>(matches.Count + 3);
            for (var index = 0; index < Math.Min(matches.Count, MaxFormattedResults); index++)
            {
                var match = matches[index];
                lines.Add($"- {match.name} ({match.GetType().Name}) | {GetLocation(match)} | {BridgeObjectId.Format(match)}");
            }

            if (matches.Count > MaxFormattedResults)
            {
                lines.Add(string.Empty);
                lines.Add($"Showing the first {MaxFormattedResults} results; additional matches were omitted.");
                lines.Add("More specific queries return a narrower result set.");
            }

            if (includeHint && matches.Count > 1)
            {
                lines.Add(string.Empty);
                lines.Add("Multiple objects match your query.");
                lines.Add($"Rerun with {BridgeObjectId.Prefix}<number> to select a specific match.");
            }

            return string.Join("\n", lines);
        }

        internal static string GetHierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return "/" + string.Join("/", names);
        }

        static IEnumerable<T> Filter<T>(IEnumerable<Object> objects) where T : Object
        {
            foreach (var candidate in objects)
            {
                if (candidate is T typed)
                {
                    yield return typed;
                    continue;
                }

                if (candidate is GameObject gameObject
                    && typeof(Component).IsAssignableFrom(typeof(T)))
                    foreach (var component in gameObject.GetComponents(typeof(T)))
                        if (component is T typedComponent)
                            yield return typedComponent;
            }
        }

        static T SelectSingle<T>(string query, IEnumerable<T> values)
        {
            using var enumerator = values.GetEnumerator();
            if (!enumerator.MoveNext())
                throw new InvalidOperationException($"No matches for '{query}'.");

            var value = enumerator.Current;
            if (!enumerator.MoveNext())
                return value;

            var matches = values.Cast<Object>().Take(MaxFormattedResults + 1).ToArray();
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
                || !BridgeObjectId.TryParse(query.Substring(prefixLength), out var objectId))
                return false;

            value = Resources.FindObjectsOfTypeAll<Object>()
                .FirstOrDefault(candidate =>
                    candidate != null
                    && IsSupportedType(candidate)
                    && BridgeObjectId.Get(candidate) == objectId
                );
            return true;
        }

        static List<GameObject> ResolveHierarchyPath(string query)
        {
            var segments = query.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var matches = new List<GameObject>();
            if (segments.Length == 0)
                return matches;

            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (!string.Equals(root.name, segments[0], StringComparison.Ordinal))
                        continue;

                    var current = root.transform;
                    var matched = true;
                    for (var index = 1; index < segments.Length; index++)
                    {
                        current = FindChild(current, segments[index]);
                        if (current != null)
                            continue;

                        matched = false;
                        break;
                    }

                    if (matched && current != null)
                        matches.Add(current.gameObject);
                }
            }

            return matches;
        }

        static Transform? FindChild(Transform parent, string name)
        {
            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index);
                if (string.Equals(child.name, name, StringComparison.Ordinal))
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
            var nameParts = new List<string>();
            foreach (var part in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if ((part.StartsWith("t:", StringComparison.OrdinalIgnoreCase)
                     || part.StartsWith("t=", StringComparison.OrdinalIgnoreCase))
                    && part.Length > 2)
                {
                    requestedType = ConduitReflect.ResolveType(part.Substring(2));
                    unresolvedType |= requestedType == null;
                    continue;
                }

                nameParts.Add(part.TrimStart('+'));
            }

            nameQuery = string.Join(" ", nameParts);
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

        static string GetLocation(Object target)
        {
            if (target is GameObject gameObject)
                return FormatSceneLocation(gameObject.scene, GetHierarchyPath(gameObject));
            if (target is Component component)
                return FormatSceneLocation(component.gameObject.scene, GetHierarchyPath(component.gameObject));
            return target.GetType().Name;
        }

        static string FormatSceneLocation(Scene scene, string hierarchyPath)
        {
            var sceneName = !string.IsNullOrWhiteSpace(scene.path)
                ? scene.path
                : string.IsNullOrWhiteSpace(scene.name)
                    ? "<unsaved scene>"
                    : $"<unsaved scene:{scene.name}>";
            return $"{sceneName}:{hierarchyPath}";
        }
    }
}
