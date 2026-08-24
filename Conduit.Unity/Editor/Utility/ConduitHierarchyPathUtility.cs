#nullable enable

using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static class ConduitHierarchyPathUtility
    {
        internal static string FormatScenePath(Scene scene, string unsavedLabel)
        {
            var path = scene.path;
            if (!string.IsNullOrWhiteSpace(path))
                return path;

            var name = scene.name;
            return string.IsNullOrWhiteSpace(name)
                ? $"<{unsavedLabel}>"
                : $"<{unsavedLabel}:{name}>";
        }

        /// <summary>Builds a slash-delimited hierarchy path for a transform.</summary>
        internal static string BuildHierarchyPath(Transform transform)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            AppendHierarchySegment(builder, transform, 0);
            return builder.ToString();

            static void AppendHierarchySegment(StringBuilder builder, Transform current, int depth)
            {
                // cap call depth while retaining the cheaper recursive path for normal scene hierarchies.
                if (depth == 256)
                {
                    AppendDeepAncestors(builder, current);
                    return;
                }

                var parent = current.parent;
                if (parent != null)
                {
                    AppendHierarchySegment(builder, parent, depth + 1);
                    builder.Append('/');
                }

                builder.Append(current.name);
            }

            static void AppendDeepAncestors(StringBuilder builder, Transform current)
            {
                using var pooledAncestors = ConduitPool.GetPooledList<Transform>(out var ancestors);
                for (Transform? ancestor = current; ancestor != null; ancestor = ancestor.parent)
                    ancestors.Add(ancestor);

                for (var index = ancestors.Count - 1; index >= 0; index--)
                {
                    if (index < ancestors.Count - 1)
                        builder.Append('/');

                    builder.Append(ancestors[index].name);
                }
            }
        }

    }
}
