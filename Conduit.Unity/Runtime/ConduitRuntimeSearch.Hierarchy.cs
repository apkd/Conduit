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
    public static partial class ConduitRuntimeSearch
    {
        internal static string GetHierarchyPath(GameObject gameObject)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append('/');
            AppendHierarchyPath(builder, gameObject.transform, 0);
            return builder.ToString();
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
}

