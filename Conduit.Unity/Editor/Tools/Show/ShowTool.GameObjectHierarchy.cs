#nullable enable

using System.Text;
using UnityEngine;

namespace Conduit
{
    static partial class ShowTool
    {
        static void AppendHierarchy(StringBuilder builder, Transform transform)
        {
            AppendHierarchyRoot(builder, transform);
        }

        static void AppendHierarchyRoot(StringBuilder builder, Transform transform)
        {
            builder.AppendLine(transform.name);

            var childCount = transform.childCount;
            for (var index = 0; index < childCount; index++)
                AppendHierarchyNode(builder, transform.GetChild(index), string.Empty, index == childCount - 1);
        }

        static void AppendHierarchyNode(StringBuilder builder, Transform transform, string prefix, bool isLast)
        {
            builder.Append(prefix);
            builder.Append(isLast ? "└─" : "├─");
            builder.AppendLine(transform.name);

            var childPrefix = prefix + (isLast ? "  " : "│ ");
            var childCount = transform.childCount;
            for (var index = 0; index < childCount; index++)
                AppendHierarchyNode(builder, transform.GetChild(index), childPrefix, index == childCount - 1);
        }

        static void AppendGameObjectHierarchyDetails(
            StringBuilder builder,
            GameObject gameObject,
            bool includeObjectIds = true)
        {
            if (ShouldUseCompactHierarchy(gameObject.transform))
            {
                AppendGameObject(builder, gameObject, includeObjectIds);
                AppendCompactGameObjectHierarchy(builder, gameObject.transform, includeObjectIds);
                return;
            }

            builder.AppendLine("Hierarchy:");
            AppendHierarchy(builder, gameObject.transform);
            builder.AppendLine();

            using var pooledTransforms = ConduitPool.GetPooledList<Transform>(out var transforms);
            gameObject.GetComponentsInChildren(true, transforms);
            foreach (var transform in transforms)
                AppendGameObject(builder, transform.gameObject, includeObjectIds);
        }

        static bool ShouldUseCompactHierarchy(Transform transform)
            => CountHierarchyGameObjects(transform, CompactHierarchyGameObjectThreshold + 1) > CompactHierarchyGameObjectThreshold;

        static int CountHierarchyGameObjects(Transform transform, int limit)
        {
            var count = 1;
            if (count >= limit)
                return count;

            var childCount = transform.childCount;
            for (var index = 0; index < childCount; index++)
            {
                count += CountHierarchyGameObjects(transform.GetChild(index), limit - count);
                if (count >= limit)
                    return count;
            }

            return count;
        }

        static void AppendCompactGameObjectHierarchy(
            StringBuilder builder,
            Transform root,
            bool includeObjectIds)
        {
            var componentIdentifiers = BuildHierarchyComponentIdentifiers(root);
            AppendComponentLegend(builder, componentIdentifiers);

            builder.AppendLine("Hierarchy:");
            AppendSceneHierarchyRoot(builder, root, componentIdentifiers, includeObjectIds);
        }

        static void AppendGameObject(
            StringBuilder builder,
            GameObject gameObject,
            bool includeObjectIds = true)
        {
            builder.Append("GameObject: ")
                .Append(ConduitHierarchyPathUtility.BuildHierarchyPath(gameObject.transform));
            if (includeObjectIds)
                builder.Append(" [").Append(ConduitObjectId.FormatObjectId(gameObject)).Append(']');
            builder.AppendLine();

            using var pooledComponents = ConduitPool.GetPooledList<Component>(out var components);
            gameObject.GetComponents(components);
            if (components.Count == 0)
            {
                builder.AppendLine("  Components: <none>");
                builder.AppendLine();
                return;
            }

            builder.AppendLine("  Components:");
            foreach (var component in components)
            {
                if (component == null)
                {
                    builder.AppendLine("  - Missing Component");
                    continue;
                }

                builder.Append("  - ").Append(component.GetType().FullName);
                if (includeObjectIds)
                    builder.Append(" [").Append(ConduitObjectId.FormatObjectId(component)).Append(']');
                builder.AppendLine();
                AppendSerializableFields(builder, component, 4);
                AppendNonSerializableFields(builder, component, 4);
            }

            builder.AppendLine();
        }
    }
}
