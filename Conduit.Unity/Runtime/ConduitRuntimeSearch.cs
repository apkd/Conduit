#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Conduit.Runtime
{
    /// <summary>Finds loaded player objects for generated execute_code snippets.</summary>
    public static class ConduitRuntimeSearch
    {
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

            if (normalized[0] == '/' && ResolveHierarchyPath(normalized) is { } byPath)
                return new() { byPath };

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
                    : RuntimeObjectId.Get(left).CompareTo(RuntimeObjectId.Get(right));
            });
            return results;
        }

        internal static Object ResolveOne(string query)
            => SelectSingle<Object>(query, ResolveMany(query));

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
                    && typeof(Component).IsAssignableFrom(typeof(T))
                    && gameObject.GetComponent(typeof(T)) is T component)
                    yield return component;
            }
        }

        static T SelectSingle<T>(string query, IEnumerable<T> values)
        {
            using var enumerator = values.GetEnumerator();
            if (!enumerator.MoveNext())
                throw new InvalidOperationException($"No loaded Unity object matches '{query}'.");

            var value = enumerator.Current;
            if (enumerator.MoveNext())
                throw new InvalidOperationException($"The query '{query}' matches multiple loaded Unity objects.");

            return value;
        }

        static bool TryResolveObjectId(string query, out Object? value)
        {
            value = null;
            var prefixLength = query.StartsWith("eid:", StringComparison.OrdinalIgnoreCase)
                || query.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
                ? query.IndexOf(':') + 1
                : 0;
            if (prefixLength == 0
                || !ulong.TryParse(
                    query.Substring(prefixLength),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var objectId
                ))
                return false;

            value = Resources.FindObjectsOfTypeAll<Object>()
                .FirstOrDefault(candidate =>
                    candidate != null
                    && IsInspectable(candidate)
                    && RuntimeObjectId.Get(candidate) == objectId
                );
            return true;
        }

        static GameObject? ResolveHierarchyPath(string query)
        {
            var segments = query.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return null;

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
                        return current.gameObject;
                }
            }

            return null;
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
                    requestedType = ConduitRuntimeReflect.ResolveType(part.Substring(2));
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
    }

    static class RuntimeObjectId
    {
#if UNITY_6000_2_OR_NEWER
        public const string Prefix = "eid:";
#else
        public const string Prefix = "id:";
#endif

        public static ulong Get(Object target)
        {
#if UNITY_6000_4_OR_NEWER
            return EntityId.ToULong(target.GetEntityId());
#elif UNITY_6000_2_OR_NEWER
            return unchecked((uint)(int)target.GetEntityId());
#else
            return unchecked((uint)target.GetInstanceID());
#endif
        }

        public static string Format(Object target)
            => Prefix + Get(target).ToString(CultureInfo.InvariantCulture);
    }
}
