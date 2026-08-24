#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using static System.StringComparison;

namespace Conduit
{
    static partial class ShowTool
    {
        static string DebugScene(Scene scene)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine($"Scene: {FormatSceneName(scene)}");
            builder.AppendLine();

            using var pooledRoots = ConduitPool.GetPooledList<GameObject>(out var roots);
            scene.GetRootGameObjects(roots);
            var componentIdentifiers = BuildSceneComponentIdentifiers(roots);
            AppendComponentLegend(builder, componentIdentifiers);

            builder.AppendLine("Hierarchy:");
            foreach (var root in roots)
                AppendSceneHierarchyRoot(builder, root.transform, componentIdentifiers);

            return builder.ToTrimmedString();
        }

        static Scene TryGetLoadedScene(string assetPath)
        {
            var sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid())
                    if (scene.isLoaded)
                        if (string.Equals(scene.path, assetPath, OrdinalIgnoreCase))
                            return scene;
            }

            return default;
        }

        static Dictionary<Type, string> BuildSceneComponentIdentifiers(List<GameObject> sceneRoots)
        {
            using var pooledRoots = ConduitPool.GetPooledList<Transform>(out var roots);
            foreach (var root in sceneRoots)
                roots.Add(root.transform);

            return BuildComponentIdentifiers(roots);
        }

        static Dictionary<Type, string> BuildHierarchyComponentIdentifiers(Transform root)
        {
            using var pooledRoots = ConduitPool.GetPooledList<Transform>(out var roots);
            roots.Add(root);
            return BuildComponentIdentifiers(roots);
        }

        static Dictionary<Type, string> BuildComponentIdentifiers(List<Transform> roots)
        {
            using var pooledTypes = ConduitPool.GetPooledList<Type>(out var types);
            using var pooledSeenTypes = ConduitPool.GetPooledSet<Type>(out var seenTypes);
            using var pooledComponents = ConduitPool.GetPooledList<Component>(out var components);
            foreach (var root in roots)
            {
                components.Clear();
                root.GetComponentsInChildren(true, components);
                foreach (var component in components)
                {
                    if (component is null or Transform or RectTransform)
                        continue;

                    var componentType = component.GetType();
                    if (seenTypes.Add(componentType))
                        types.Add(componentType);
                }
            }

            types.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

            var identifiers = new Dictionary<Type, string>(types.Count);
            using var pooledUsed = ConduitPool.GetPooledSet<string>(out var used);
            foreach (var type in types)
            {
                var identifier = CreateComponentIdentifier(type.Name, used);
                identifiers.Add(type, identifier);
                used.Add(identifier);
            }
            return identifiers;
        }

        static void AppendComponentLegend(StringBuilder builder, IReadOnlyDictionary<Type, string> componentIdentifiers)
        {
            if (componentIdentifiers.Count == 0)
                return;

            builder.AppendLine("Components:");
            using var pooledEntries = ConduitPool.GetPooledList<KeyValuePair<Type, string>>(out var entries);
            foreach (var entry in componentIdentifiers)
                entries.Add(entry);

            entries.Sort(static (left, right) =>
                {
                    var identifierComparison = StringComparer.Ordinal.Compare(left.Value, right.Value);
                    return identifierComparison != 0
                        ? identifierComparison
                        : StringComparer.Ordinal.Compare(left.Key.Name, right.Key.Name);
                }
            );

            foreach (var entry in entries)
                builder.Append(entry.Value).Append('=').AppendLine(entry.Key.Name);

            builder.AppendLine();
        }

        static string CreateComponentIdentifier(string componentName, ISet<string> used)
        {
            if (commonComponentIdentifiers.TryGetValue(componentName, out var predefined))
                return predefined;

            string baseIdentifier = BuildGeneratedIdentifier(componentName);
            string candidate = baseIdentifier;
            int suffix = 2;
            while (used.Contains(candidate))
            {
                candidate = baseIdentifier + suffix.ToString(CultureInfo.InvariantCulture);
                suffix++;
            }

            return candidate;
        }

        static string BuildGeneratedIdentifier(string componentName)
        {
            if (string.IsNullOrWhiteSpace(componentName))
                return "CMP";

            using var pooledInitials = ConduitPool.GetStringBuilder(out var initials);
            for (int index = 0; index < componentName.Length; index++)
            {
                var character = componentName[index];
                if (!char.IsLetterOrDigit(character))
                    continue;

                var isWordStart
                    = index == 0 ||
                      char.IsUpper(character) && (
                          char.IsLower(componentName[index - 1])
                          || index + 1 < componentName.Length && char.IsLower(componentName[index + 1]));

                if (isWordStart)
                    initials.Append(char.ToUpperInvariant(character));
            }

            if (initials.Length == 0)
                return componentName[..Math.Min(3, componentName.Length)].ToUpperInvariant();

            if (initials.Length == 1)
                return componentName[..Math.Min(3, componentName.Length)].ToUpperInvariant();

            return initials.ToString();
        }

        static void AppendSceneHierarchyRoot(
            StringBuilder builder,
            Transform transform,
            IReadOnlyDictionary<Type, string> componentIdentifiers,
            bool includeObjectIds = true)
        {
            using var pooledPending = ConduitPool.GetPooledList<(
                Transform Transform,
                int Depth,
                bool IsLast
            )>(out var pending);
            using var pooledLastAtDepth = ConduitPool.GetPooledList<bool>(out var lastAtDepth);
            using var pooledIdentifiers = ConduitPool.GetPooledList<ComponentIdentifierCount>(out var identifiers);
            using var pooledComponents = ConduitPool.GetPooledList<Component>(out var components);
            AppendSceneHierarchyLine(
                builder,
                transform,
                componentIdentifiers,
                includeObjectIds,
                identifiers,
                components
            );
            var rootChildCount = transform.childCount;
            for (var index = rootChildCount - 1; index >= 0; --index)
                pending.Add((transform.GetChild(index), 0, index == rootChildCount - 1));

            while (pending.Count > 0)
            {
                var lastIndex = pending.Count - 1;
                var (current, depth, isLast) = pending[lastIndex];
                pending.RemoveAt(lastIndex);
                if (lastAtDepth.Count == depth)
                    lastAtDepth.Add(isLast);
                else
                    lastAtDepth[depth] = isLast;

                for (var index = 0; index < depth; ++index)
                    builder.Append(lastAtDepth[index] ? "  " : "│ ");
                builder.Append(isLast ? "└─" : "├─");
                AppendSceneHierarchyLine(
                    builder,
                    current,
                    componentIdentifiers,
                    includeObjectIds,
                    identifiers,
                    components
                );

                var childCount = current.childCount;
                for (var childIndex = childCount - 1; childIndex >= 0; --childIndex)
                    pending.Add((
                        current.GetChild(childIndex),
                        depth + 1,
                        childIndex == childCount - 1
                    ));
            }
        }

        static void AppendSceneHierarchyLine(
            StringBuilder builder,
            Transform transform,
            IReadOnlyDictionary<Type, string> componentIdentifiers,
            bool includeObjectIds,
            List<ComponentIdentifierCount> identifiers,
            List<Component> components)
        {
            var gameObject = transform.gameObject;
            builder.Append(gameObject.name);
            var hasMetadata = false;
            if (!gameObject.activeInHierarchy)
                AppendSceneHierarchyMetadata(builder, "inactive", ref hasMetadata);

            if (includeObjectIds)
                AppendSceneHierarchyMetadata(builder, ConduitObjectId.FormatObjectId(gameObject), ref hasMetadata);

            AppendSceneComponentIdentifiers(
                builder,
                gameObject,
                componentIdentifiers,
                identifiers,
                components,
                ref hasMetadata
            );

            builder.AppendLine(hasMetadata ? "]" : string.Empty);
        }

        static void AppendSceneComponentIdentifiers(
            StringBuilder builder,
            GameObject gameObject,
            IReadOnlyDictionary<Type, string> componentIdentifiers,
            List<ComponentIdentifierCount> identifiers,
            List<Component> components,
            ref bool hasMetadata)
        {
            identifiers.Clear();
            components.Clear();
            gameObject.GetComponents(components);
            foreach (var component in components)
            {
                if (component is null or Transform or RectTransform)
                    continue;

                var componentType = component.GetType();
                if (!componentIdentifiers.TryGetValue(componentType, out var identifier))
                    continue;

                var index = FindComponentIndex(identifiers, componentType);
                if (index < 0)
                {
                    identifiers.Add(new(componentType, identifier));
                    continue;
                }

                var count = identifiers[index];
                count.Count++;
                identifiers[index] = count;
            }

            foreach (var identifier in identifiers)
            {
                if (identifier.Count >= 3)
                {
                    builder.Append(hasMetadata ? " | " : " [")
                        .Append(identifier.Identifier)
                        .Append(" ×")
                        .Append(identifier.Count);
                    hasMetadata = true;
                    continue;
                }

                for (var index = 0; index < identifier.Count; index++)
                    AppendSceneHierarchyMetadata(builder, identifier.Identifier, ref hasMetadata);
            }
        }

        static void AppendSceneHierarchyMetadata(StringBuilder builder, string value, ref bool hasMetadata)
        {
            builder.Append(hasMetadata ? " | " : " [");
            builder.Append(value);
            hasMetadata = true;
        }

        static int FindComponentIndex(List<ComponentIdentifierCount> identifiers, Type componentType)
        {
            for (var index = 0; index < identifiers.Count; index++)
                if (identifiers[index].ComponentType == componentType)
                    return index;

            return -1;
        }

        struct ComponentIdentifierCount
        {
            internal readonly Type ComponentType;
            internal readonly string Identifier;
            internal int Count;

            internal ComponentIdentifierCount(Type componentType, string identifier)
            {
                ComponentType = componentType;
                Identifier = identifier;
                Count = 1;
            }
        }
    }
}
