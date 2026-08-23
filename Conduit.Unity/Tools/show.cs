#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using static System.StringComparison;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class show
    {
        const int MaxStringLength = 256;
        const int MaxCollectionPreview = 4;
        const int MaxEnumerableScan = 4096;
        const int CompactHierarchyGameObjectThreshold = 8;
        static readonly ConcurrentDictionary<Type, FieldInfo[]> fieldCache = new();
        static readonly ConcurrentDictionary<Type, IndexableAccess> indexableAccessCache = new();
        static readonly ConcurrentDictionary<Type, Type> enumerableElementTypeCache = new();
        static readonly ConcurrentDictionary<Type, CustomShowAccess> customShowAccessCache = new();

        static readonly Dictionary<string, string> commonComponentIdentifiers
            = new(StringComparer.Ordinal)
            {
                ["Animator"] = "A",
                ["ArticulationBody"] = "AB",
                ["AudioDistortionFilter"] = "ADF",
                ["AudioEchoFilter"] = "AEF",
                ["AudioHighPassFilter"] = "AHPF",
                ["AudioListener"] = "AL",
                ["AudioLowPassFilter"] = "ALPF",
                ["AudioReverbFilter"] = "ARF",
                ["AudioReverbZone"] = "ARZ",
                ["AudioSource"] = "AS",
                ["BillboardRenderer"] = "BBR",
                ["BoxCollider"] = "BC",
                ["BoxCollider2D"] = "BC2D",
                ["Button"] = "BTN",
                ["Camera"] = "CAM",
                ["Canvas"] = "CV",
                ["CanvasGroup"] = "CG",
                ["CanvasRenderer"] = "CR",
                ["CanvasScaler"] = "CS",
                ["CapsuleCollider"] = "CC",
                ["CapsuleCollider2D"] = "CC2D",
                ["CharacterController"] = "CHC",
                ["CharacterJoint"] = "CJ",
                ["Collider"] = "C",
                ["Collider2D"] = "C2D",
                ["Cloth"] = "CLT",
                ["CircleCollider2D"] = "C2D",
                ["ConfigurableJoint"] = "CFJ",
                ["ConstantForce"] = "CF",
                ["ContentSizeFitter"] = "CSF",
                ["Dropdown"] = "DDN",
                ["EdgeCollider2D"] = "EC2D",
                ["EventSystem"] = "ES",
                ["FixedJoint"] = "FJ",
                ["FixedJoint2D"] = "FJ2D",
                ["GraphicRaycaster"] = "GR",
                ["GridLayoutGroup"] = "GLG",
                ["HingeJoint"] = "HJ",
                ["HingeJoint2D"] = "HJ2D",
                ["HorizontalLayoutGroup"] = "HLG",
                ["Image"] = "IMG",
                ["InputField"] = "IF",
                ["LineRenderer"] = "LR",
                ["Light"] = "LT",
                ["LightProbeGroup"] = "LPG",
                ["LayoutElement"] = "LE",
                ["MeshCollider"] = "MC",
                ["MeshFilter"] = "MF",
                ["MeshRenderer"] = "MR",
                ["NavMeshAgent"] = "NA",
                ["NavMeshObstacle"] = "NO",
                ["OffMeshLink"] = "OML",
                ["ParticleSystem"] = "PS",
                ["ParticleSystemRenderer"] = "PSR",
                ["PolygonCollider2D"] = "PC2D",
                ["RawImage"] = "RI",
                ["RectTransform"] = "RT",
                ["ReflectionProbe"] = "RP",
                ["Rigidbody"] = "R",
                ["Rigidbody2D"] = "R2D",
                ["ScrollRect"] = "SCRL",
                ["Slider"] = "SLD",
                ["SkinnedMeshRenderer"] = "SMR",
                ["SphereCollider"] = "SC",
                ["SphereCollider2D"] = "SC2D",
                ["SpringJoint"] = "SJ",
                ["SpringJoint2D"] = "SJ2D",
                ["SpriteRenderer"] = "SR",
                ["Text"] = "TXT",
                ["TextMeshProUGUI"] = "TMUG",
                ["Terrain"] = "TER",
                ["TerrainCollider"] = "TC",
                ["Toggle"] = "TGL",
                ["TrailRenderer"] = "TR",
                ["UniversalAdditionalCameraData"] = "UACD",
                ["UniversalAdditionalLightData"] = "UALD",
                ["VerticalLayoutGroup"] = "VLG",
                ["VideoPlayer"] = "VP",
                ["WheelCollider"] = "WC",
                ["Volume"] = "VOL",
            };

        public static string Show(string query)
        {
            var result = ConduitSearchUtility.Resolve(query) switch
            {
                { Count: 0 }           => ConduitSearchUtility.FormatNoMatches(query),
                { Count: 1 } matches   => DebugResolvedObject(matches[0]),
                { Count: > 1 } matches => ConduitSearchUtility.FormatMatches(matches, includeHint: true),
            };
            return result.Replace("\r\n", "\n");
        }

        static string DebugResolvedObject(ResolvedObjectMatch match)
        {
            var target = match.Target;
            if (TryGetCustomShowText(target, out var customShowText))
                return customShowText;

            if (target is EditorWindow window)
                return DebugEditorWindow(window);

            var assetPath = match.AssetPath ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(assetPath))
            {
                if (assetPath.EndsWith(".unity", OrdinalIgnoreCase))
                    return DebugSceneAsset(assetPath);

                if (match.Source == ResolvedObjectMatchSource.AssetPath)
                    return assetPath.EndsWith(".prefab", OrdinalIgnoreCase)
                        ? DebugPrefab(assetPath)
                        : DebugAsset(assetPath);
            }

            return target switch
            {
                GameObject gameObject => DebugExactGameObject(gameObject, assetPath),
                _                     => DebugLooseObject(target, assetPath),
            };
        }

        static bool TryGetCustomShowText(Object target, out string customShowText)
        {
            customShowText = string.Empty;
            if (target == null)
                return false;

            var method = customShowAccessCache
                .GetOrAdd(target.GetType(), static type => new(type.GetMethod(
                    "ToStringForMCP",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null
                )))
                .Method;
            if (method == null || method.ReturnType != typeof(string))
                return false;

            customShowText = (string?)method.Invoke(target, null) ?? string.Empty;
            return true;
        }

        static string DebugEditorWindow(EditorWindow window)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            var position = window.position;
            builder.AppendLine($"Editor Window: {ConduitSearchUtility.GetEditorWindowDisplayName(window)}");
            builder.AppendLine($"Type: {window.GetType().FullName}");
            builder.AppendLine($"Title: {ConduitSearchUtility.GetEditorWindowTitle(window)}");
            builder.AppendLine($"Object: {ConduitUtility.FormatObjectId(window)}");
            builder.AppendLine($"Focused: {(EditorWindow.focusedWindow == window ? "yes" : "no")}");
            builder.AppendLine($"Docked: {(window.docked ? "yes" : "no")}");
            builder.Append("Position: x=")
                .AppendInvariant(position.x, "0.###")
                .Append(", y=")
                .AppendInvariant(position.y, "0.###")
                .Append(", width=")
                .AppendInvariant(position.width, "0.###")
                .Append(", height=")
                .AppendInvariant(position.height, "0.###")
                .AppendLine();
            return builder.TrimEnd().ToString();
        }

        static string DebugSceneAsset(string assetPath)
        {
            var loadedScene = TryGetLoadedScene(assetPath);
            if (loadedScene.IsValid())
                if (loadedScene.isLoaded)
                    return DebugScene(loadedScene);

            var sceneAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine($"Asset: {assetPath}");
            builder.AppendLine($"Main Object: {DescribeObject(sceneAsset, assetPath)}");
            if (sceneAsset != null)
                AppendObjectIdentifiers(builder, sceneAsset, 0, includeGuid: true, assetPath: assetPath);

            builder.AppendLine("Cannot inspect the hierarchy because the scene is closed.");
            return builder.TrimEnd().ToString();
        }

        static string DebugPrefab(string assetPath)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                builder.AppendLine($"Asset: {assetPath}");
                builder.AppendLine($"Main Object: {DescribeObject(root)}");
                builder.AppendLine("Object IDs are omitted because prefab contents are temporary.");
                builder.AppendLine();
                AppendGameObjectHierarchyDetails(builder, root, includeObjectIds: false);

                using var pooledSubassets = ConduitUtility.GetPooledList<Object>(out var subassets);
                foreach (var assetObject in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (assetObject is GameObject or Component or null)
                        continue;

                    if (assetObject == root)
                        continue;

                    subassets.Add(assetObject);
                }

                if (subassets.Count > 0)
                {
                    builder.AppendLine("Imported Subassets:");
                    foreach (var subasset in subassets)
                        AppendAssetObject(builder, subasset, "Subasset", assetPath);
                }

                return builder.TrimEnd().ToString();
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static string DebugAsset(string assetPath)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            using var pooledAssets = ConduitUtility.GetPooledList<Object>(out var allAssets);
            foreach (var assetObject in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                if (assetObject != null)
                    allAssets.Add(assetObject);

            var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);

            builder.AppendLine($"Asset: {assetPath}");
            builder.AppendLine($"Main Object: {DescribeObject(mainAsset, assetPath)}");
            var subassetCount = Math.Max(0, allAssets.Count - 1);
            if (subassetCount > 0)
                builder.AppendLine($"Imported Subassets: {subassetCount}");

            builder.AppendLine();

            if (mainAsset != null)
                AppendAssetObject(builder, mainAsset, "Main Object", assetPath);

            foreach (var subasset in allAssets)
                if (subasset != mainAsset)
                    AppendAssetObject(builder, subasset, "Subasset", assetPath);

            return builder.TrimEnd().ToString();
        }

        static string DebugExactGameObject(GameObject gameObject, string assetPath)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine($"Object: {DescribeObject(gameObject, assetPath)}");
            builder.AppendLine(!string.IsNullOrWhiteSpace(assetPath) ? $"Asset: {assetPath}" : $"Scene: {FormatSceneName(gameObject.scene)}");
            builder.AppendLine();
            AppendGameObjectHierarchyDetails(builder, gameObject);

            return builder.TrimEnd().ToString();
        }

        static string DebugLooseObject(Object target, string assetPath)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            if (!string.IsNullOrWhiteSpace(assetPath))
                builder.AppendLine($"Asset: {assetPath}");
            else if (target is Component component)
                builder.AppendLine($"Scene: {FormatSceneName(component.gameObject.scene)}");

            builder.AppendLine($"Object: {DescribeObject(target, assetPath)}");
            AppendObjectIdentifiers(builder, target, 0, includeGuid: true, assetPath: assetPath);
            AppendSerializableFields(builder, target, 2);
            AppendNonSerializableFields(builder, target, 2);
            return builder.TrimEnd().ToString();
        }

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

        static string DebugScene(Scene scene)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine($"Scene: {FormatSceneName(scene)}");
            builder.AppendLine();

            using var pooledRoots = ConduitUtility.GetPooledList<GameObject>(out var roots);
            scene.GetRootGameObjects(roots);
            var componentIdentifiers = BuildSceneComponentIdentifiers(roots);
            AppendComponentLegend(builder, componentIdentifiers);

            builder.AppendLine("Hierarchy:");
            foreach (var root in roots)
                AppendSceneHierarchyRoot(builder, root.transform, componentIdentifiers);

            return builder.TrimEnd().ToString();
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
            using var pooledRoots = ConduitUtility.GetPooledList<Transform>(out var roots);
            foreach (var root in sceneRoots)
                roots.Add(root.transform);

            return BuildComponentIdentifiers(roots);
        }

        static Dictionary<Type, string> BuildHierarchyComponentIdentifiers(Transform root)
        {
            using var pooledRoots = ConduitUtility.GetPooledList<Transform>(out var roots);
            roots.Add(root);
            return BuildComponentIdentifiers(roots);
        }

        static Dictionary<Type, string> BuildComponentIdentifiers(List<Transform> roots)
        {
            using var pooledTypes = ConduitUtility.GetPooledList<Type>(out var types);
            using var pooledSeenTypes = ConduitUtility.GetPooledSet<Type>(out var seenTypes);
            using var pooledComponents = ConduitUtility.GetPooledList<Component>(out var components);
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
            using var pooledUsed = ConduitUtility.GetPooledSet<string>(out var used);
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
            using var pooledEntries = ConduitUtility.GetPooledList<KeyValuePair<Type, string>>(out var entries);
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

            using var pooledInitials = ConduitUtility.GetStringBuilder(out var initials);
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
            using var pooledPending = ConduitUtility.GetPooledList<(
                Transform Transform,
                int Depth,
                bool IsLast
            )>(out var pending);
            using var pooledLastAtDepth = ConduitUtility.GetPooledList<bool>(out var lastAtDepth);
            using var pooledIdentifiers = ConduitUtility.GetPooledList<ComponentIdentifierCount>(out var identifiers);
            using var pooledComponents = ConduitUtility.GetPooledList<Component>(out var components);
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
                AppendSceneHierarchyMetadata(builder, ConduitUtility.FormatObjectId(gameObject), ref hasMetadata);

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
            public readonly Type ComponentType;
            public readonly string Identifier;
            public int Count;

            public ComponentIdentifierCount(Type componentType, string identifier)
            {
                ComponentType = componentType;
                Identifier = identifier;
                Count = 1;
            }
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

            using var pooledTransforms = ConduitUtility.GetPooledList<Transform>(out var transforms);
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
                .Append(ConduitUtility.BuildHierarchyPath(gameObject.transform));
            if (includeObjectIds)
                builder.Append(" [").Append(ConduitUtility.FormatObjectId(gameObject)).Append(']');
            builder.AppendLine();

            using var pooledComponents = ConduitUtility.GetPooledList<Component>(out var components);
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
                    builder.Append(" [").Append(ConduitUtility.FormatObjectId(component)).Append(']');
                builder.AppendLine();
                AppendSerializableFields(builder, component, 4);
                AppendNonSerializableFields(builder, component, 4);
            }

            builder.AppendLine();
        }

        static void AppendAssetObject(
            StringBuilder builder,
            Object assetObject,
            string label,
            string assetPath)
        {
            builder.Append(label)
                .Append(": ")
                .AppendLine(DescribeObject(assetObject, assetPath));
            AppendObjectIdentifiers(builder, assetObject, 2, includeGuid: true, assetPath: assetPath);
            AppendSerializableFields(builder, assetObject, 2);
            AppendNonSerializableFields(builder, assetObject, 2);
            builder.AppendLine();
        }

        static void AppendSerializableFields(StringBuilder builder, Object target, int indent)
        {
            try
            {
                using var serializedObject = new SerializedObject(target);
                var iterator = serializedObject.GetIterator();
                var enterChildren = true;
                var hasAny = false;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.depth != 0 || iterator.propertyPath == "m_ObjectHideFlags")
                        continue;

                    if (!hasAny)
                    {
                        builder.Append(' ', indent);
                        builder.AppendLine("Serializable:");
                        hasAny = true;
                    }

                    AppendSerializedProperty(builder, target, iterator, indent + 2);
                }
            }
            catch (Exception exception)
            {
                builder.Append(' ', indent);
                builder.AppendLine("Serializable:");
                builder.Append(' ', indent + 2);
                builder.Append("- <unavailable: ")
                    .Append(exception.Message)
                    .AppendLine(">");
            }
        }

        static void AppendNonSerializableFields(StringBuilder builder, object target, int indent)
        {
            var fields = GetInspectableFields(target.GetType());
            var hasAny = false;
            foreach (var field in fields)
            {
                if (IsUnitySerializableField(field))
                    continue;

                if (!hasAny)
                {
                    builder.Append(' ', indent);
                    builder.AppendLine("Non-Serializable:");
                    hasAny = true;
                }

                TryFormatFieldValue(field, target, 0, out var valueText);

                builder.Append(' ', indent + 2);
                builder.Append("- ")
                    .Append(field.Name)
                    .Append(": ")
                    .AppendLine(valueText);
            }
        }

        static void AppendSerializedProperty(StringBuilder builder, Object target, SerializedProperty property, int indent)
        {
            if (property is { isArray: true, propertyType: not SerializedPropertyType.String })
            {
                builder.Append(' ', indent);
                builder.Append("- ")
                    .Append(property.name)
                    .Append(": ")
                    .AppendLine(FormatArrayProperty(target, property));
                return;
            }

            if (property is { hasVisibleChildren: true, propertyType: SerializedPropertyType.Generic })
            {
                builder.Append(' ', indent);
                builder.Append("- ").Append(property.name).AppendLine(":");
                AppendImmediateChildren(builder, target, property, indent + 2);

                return;
            }

            builder.Append(' ', indent);
            builder.Append("- ")
                .Append(property.name)
                .Append(": ")
                .AppendLine(FormatSerializedValue(property));
        }

        static void AppendImmediateChildren(
            StringBuilder builder,
            Object target,
            SerializedProperty property,
            int indent)
        {
            var cursor = property.Copy();
            var end = cursor.GetEndProperty();
            var enterChildren = true;
            while (cursor.NextVisible(enterChildren) && !SerializedProperty.EqualContents(cursor, end))
            {
                enterChildren = false;
                if (cursor.depth == property.depth + 1)
                    AppendSerializedProperty(builder, target, cursor, indent);
            }
        }

        static string FormatArrayProperty(Object target, SerializedProperty property)
        {
            var count = property.arraySize;
            var elementType = GetEnumerableElementType(ResolveDeclaredType(target.GetType(), property.propertyPath)) ?? typeof(object);
            if (count == 0)
                return elementType == typeof(bool) ? string.Empty : "[]";

            var previewCount = GetPreviewCount(elementType);
            if (elementType == typeof(bool))
            {
                using var pooledBits = ConduitUtility.GetStringBuilder(out var bits);
                var visibleCount = count <= previewCount ? count : previewCount - 1;
                for (var index = 0; index < visibleCount; ++index)
                    bits.Append(FormatBit(index));

                if (count <= previewCount)
                    return bits.ToString();

                bits.Append("...");
                bits.Append(FormatBit(count - 1));
                bits.Append(" (n=").Append(count).Append(')');
                return bits.ToString();

                char FormatBit(int index)
                    => FormatSerializedElement(target, property.GetArrayElementAtIndex(index), 1) == "true" ? '1' : '0';
            }

            using var pooledPreview = ConduitUtility.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            var visibleItems = count <= previewCount ? count : previewCount - 1;
            for (var index = 0; index < visibleItems; ++index)
                AppendPreviewItem(
                    preview,
                    ref appendedCount,
                    FormatSerializedElement(target, property.GetArrayElementAtIndex(index), 1)
                );

            if (count <= previewCount)
                return preview.Append(']').ToString();

            AppendPreviewItem(preview, ref appendedCount, "...");
            AppendPreviewItem(
                preview,
                ref appendedCount,
                FormatSerializedElement(target, property.GetArrayElementAtIndex(count - 1), 1)
            );
            return preview.Append("] (n=").Append(count).Append(')').ToString();
        }

        static string FormatSerializedElement(Object target, SerializedProperty property, int depth)
        {
            if (depth > 1)
                return $"<{property.propertyType}>";

            if (property is { isArray: true, propertyType: not SerializedPropertyType.String })
                return FormatArrayProperty(target, property);

            if (property is { hasVisibleChildren: true, propertyType: SerializedPropertyType.Generic })
            {
                using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
                builder.Append('{');
                var cursor = property.Copy();
                var end = cursor.GetEndProperty();
                var enterChildren = true;
                var childCount = 0;
                while (cursor.NextVisible(enterChildren)
                       && !SerializedProperty.EqualContents(cursor, end))
                {
                    enterChildren = false;
                    if (cursor.depth != property.depth + 1)
                        continue;

                    if (childCount < MaxCollectionPreview)
                    {
                        if (childCount > 0)
                            builder.Append(", ");

                        builder.Append(cursor.name);
                        builder.Append('=');
                        builder.Append(FormatSerializedElement(target, cursor, depth + 1));
                    }
                    childCount++;
                }

                if (childCount == 0)
                    return "{}";
                if (childCount > MaxCollectionPreview)
                    builder.Append(", ...");

                builder.Append('}');
                return builder.ToString();
            }

            return FormatSerializedValue(property);
        }

        static string FormatSerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return property.floatValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return FormatString(property.stringValue);
                case SerializedPropertyType.Color:
                    return $"rgba({property.colorValue.r:0.###}, {property.colorValue.g:0.###}, {property.colorValue.b:0.###}, {property.colorValue.a:0.###})";
                case SerializedPropertyType.ObjectReference:
                    return DescribeObject(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                    return property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                        ? property.enumDisplayNames[property.enumValueIndex]
                        : property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return FormatVector(property.vector2Value.x, property.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return FormatVector(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return FormatVector(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                case SerializedPropertyType.Rect:
                    return $"Rect(x={property.rectValue.x:0.###}, y={property.rectValue.y:0.###}, w={property.rectValue.width:0.###}, h={property.rectValue.height:0.###})";
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return FormatString(char.ConvertFromUtf32(property.intValue));
                case SerializedPropertyType.AnimationCurve:
                    return $"AnimationCurve(keys={property.animationCurveValue?.length ?? 0})";
                case SerializedPropertyType.Bounds:
                    return $"Bounds(center={FormatVector(property.boundsValue.center.x, property.boundsValue.center.y, property.boundsValue.center.z)}, size={FormatVector(property.boundsValue.size.x, property.boundsValue.size.y, property.boundsValue.size.z)})";
                case SerializedPropertyType.Gradient:
                    return "Gradient(...)";
                case SerializedPropertyType.Quaternion:
                    return FormatVector(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                case SerializedPropertyType.ExposedReference:
                    return DescribeObject(property.exposedReferenceValue);
                case SerializedPropertyType.FixedBufferSize:
                    return property.fixedBufferSize.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2Int:
                    return $"({property.vector2IntValue.x}, {property.vector2IntValue.y})";
                case SerializedPropertyType.Vector3Int:
                    return $"({property.vector3IntValue.x}, {property.vector3IntValue.y}, {property.vector3IntValue.z})";
                case SerializedPropertyType.RectInt:
                    return $"RectInt(x={property.rectIntValue.x}, y={property.rectIntValue.y}, w={property.rectIntValue.width}, h={property.rectIntValue.height})";
                case SerializedPropertyType.BoundsInt:
                    return $"BoundsInt(pos=({property.boundsIntValue.position.x}, {property.boundsIntValue.position.y}, {property.boundsIntValue.position.z}), size=({property.boundsIntValue.size.x}, {property.boundsIntValue.size.y}, {property.boundsIntValue.size.z}))";
                case SerializedPropertyType.ManagedReference:
                    return property.managedReferenceFullTypename is { Length: > 0 } ? property.managedReferenceFullTypename : "null";
                default:
                    return $"<{property.propertyType}>";
            }
        }

        static FieldInfo[] GetInspectableFields(Type type)
            => fieldCache.GetOrAdd(
                type, static targetType
                    =>
                {
                    var fields = new List<FieldInfo>();
                    var seenNames = new HashSet<string>(StringComparer.Ordinal);
                    for (var current = targetType; current != null && current != typeof(object) && current != typeof(Object); current = current.BaseType)
                    {
                        foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (field.IsDefined(typeof(CompilerGeneratedAttribute), false) || !seenNames.Add(field.Name))
                                continue;

                            fields.Add(field);
                        }
                    }

                    return fields.ToArray();
                }
            );

        static bool IsUnitySerializableField(FieldInfo field)
            => field is { IsStatic: false, IsInitOnly: false, IsNotSerialized: false } &&
               (field.IsPublic || field.IsDefined(typeof(SerializeField), false) || field.IsDefined(typeof(SerializeReference), false));

        static bool TryFormatFieldValue(FieldInfo field, object target, int depth, out string valueText)
        {
            try
            {
                valueText = FormatValue(field.GetValue(target), depth);
                return true;
            }
            catch (Exception exception)
            {
                valueText = FormatUnavailable(exception);
                return false;
            }
        }

        static string FormatValue(object? value, int depth)
        {
            if (value == null)
                return "null";

            if (value is string stringValue)
                return FormatString(stringValue);

            if (value is char charValue)
                return FormatString(charValue.ToString());

            if (value is bool boolValue)
                return boolValue ? "true" : "false";

            if (value is Enum)
                return value.ToString();

            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);

            if (value is Vector2 vector2)
                return FormatVector(vector2.x, vector2.y);

            if (value is Vector3 vector3)
                return FormatVector(vector3.x, vector3.y, vector3.z);

            if (value is Vector4 vector4)
                return FormatVector(vector4.x, vector4.y, vector4.z, vector4.w);

            if (value is Quaternion quaternion)
                return FormatVector(quaternion.x, quaternion.y, quaternion.z, quaternion.w);

            if (value is Color color)
                return $"rgba({color.r:0.###}, {color.g:0.###}, {color.b:0.###}, {color.a:0.###})";

            if (value is Rect rect)
                return $"Rect(x={rect.x:0.###}, y={rect.y:0.###}, w={rect.width:0.###}, h={rect.height:0.###})";

            if (value is Bounds bounds)
                return $"Bounds(center={FormatVector(bounds.center.x, bounds.center.y, bounds.center.z)}, size={FormatVector(bounds.size.x, bounds.size.y, bounds.size.z)})";

            if (value is Object unityObject)
                return DescribeObject(unityObject);

            if (value is IList list)
                return FormatList(list, depth + 1, GetEnumerableElementType(value.GetType()));

            if (TryFormatIndexable(value, depth + 1, out var indexableText))
                return indexableText;

            if (value is IDictionary dictionary)
                return FormatDictionary(dictionary, depth + 1);

            if (value is IEnumerable enumerable)
                return FormatEnumerable(enumerable, depth + 1, GetEnumerableElementType(value.GetType()));

            if (depth < 1)
            {
                if (SummarizeObject(value, depth + 1) is { Length: > 0 } summary)
                    return summary;
            }

            return TrimCompact(value.ToString());
        }

        static bool TryFormatIndexable(object value, int depth, out string text)
        {
            var access = indexableAccessCache.GetOrAdd(value.GetType(), CreateIndexableAccess);
            if (!access.Available)
            {
                text = string.Empty;
                return false;
            }

            text = FormatIndexable(value, depth, access);
            return true;
        }

        static IndexableAccess CreateIndexableAccess(Type type)
        {
            foreach (var candidate in type.GetInterfaces())
            {
                if (!candidate.IsGenericType
                    || candidate.GetGenericTypeDefinition().FullName != "Unity.Collections.IIndexable`1")
                    continue;

                var elementType = candidate.GetGenericArguments()[0];
                var lengthGetter = candidate.GetProperty("Length")?.GetMethod;
                var elementAt = candidate.GetMethod("ElementAt", new[] { typeof(int) });
                if (lengthGetter == null || elementAt == null)
                    continue;

                return new(
                    true,
                    elementType,
                    CreateIndexableLengthAccessor(candidate, lengthGetter),
                    CreateIndexableElementAccessor(candidate, elementAt, elementType)
                );
            }

            return IndexableAccess.Unavailable;
        }

        static Func<object, int> CreateIndexableLengthAccessor(Type indexableType, MethodInfo lengthGetter)
        {
            var method = new DynamicMethod(
                "GetIIndexableLength",
                typeof(int),
                new[] { typeof(object) },
                typeof(show).Module,
                true
            );
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, indexableType);
            il.Emit(OpCodes.Callvirt, lengthGetter);
            il.Emit(OpCodes.Ret);
            return (Func<object, int>)method.CreateDelegate(typeof(Func<object, int>));
        }

        static Func<object, int, object?> CreateIndexableElementAccessor(Type indexableType, MethodInfo elementAt, Type elementType)
        {
            var method = new DynamicMethod(
                "GetIIndexableElement",
                typeof(object),
                new[] { typeof(object), typeof(int) },
                typeof(show).Module,
                true
            );
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, indexableType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Callvirt, elementAt);
            il.Emit(OpCodes.Ldobj, elementType);
            if (elementType.IsValueType)
                il.Emit(OpCodes.Box, elementType);

            il.Emit(OpCodes.Ret);
            return (Func<object, int, object?>)method.CreateDelegate(typeof(Func<object, int, object?>));
        }

        static string FormatIndexable(object value, int depth, IndexableAccess access)
        {
            var getLength = access.GetLength!;
            var getElement = access.GetElement!;
            var count = getLength(value);
            if (count <= 0)
                return access.ElementType == typeof(bool) ? string.Empty : "[]";

            var previewCount = GetPreviewCount(access.ElementType);
            if (access.ElementType == typeof(bool))
            {
                using var pooledBits = ConduitUtility.GetStringBuilder(out var bits);
                var visibleCount = count <= previewCount ? count : previewCount - 1;
                for (var index = 0; index < visibleCount; index++)
                    bits.Append(getElement(value, index) is true ? '1' : '0');

                if (count <= previewCount)
                    return bits.ToString();

                var lastBit = getElement(value, count - 1) is true ? '1' : '0';
                bits.Append("...").Append(lastBit).Append(" (n=").Append(count).Append(')');
                return bits.ToString();
            }

            using var pooledPreview = ConduitUtility.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            var visibleItems = count <= previewCount ? count : previewCount - 1;
            for (var index = 0; index < visibleItems; index++)
                AppendPreviewItem(
                    preview,
                    ref appendedCount,
                    FormatValue(getElement(value, index), depth)
                );

            if (count <= previewCount)
                return preview.Append(']').ToString();

            AppendPreviewItem(preview, ref appendedCount, "...");
            AppendPreviewItem(
                preview,
                ref appendedCount,
                FormatValue(getElement(value, count - 1), depth)
            );
            return preview.Append("] (n=").Append(count).Append(')').ToString();
        }

        static string FormatList(IList list, int depth, Type elementType)
        {
            var count = list.Count;
            if (count == 0)
                return elementType == typeof(bool) ? string.Empty : "[]";

            var previewCount = GetPreviewCount(elementType);
            var visibleCount = count <= previewCount ? count : previewCount - 1;
            if (elementType == typeof(bool))
            {
                using var pooledBits = ConduitUtility.GetStringBuilder(out var bits);
                for (var index = 0; index < visibleCount; ++index)
                    bits.Append(list[index] is true ? '1' : '0');

                if (count <= previewCount)
                    return bits.ToString();

                bits.Append("...");
                bits.Append(list[count - 1] is true ? '1' : '0');
                bits.Append(" (n=").Append(count).Append(')');
                return bits.ToString();
            }

            using var pooledPreview = ConduitUtility.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            for (var index = 0; index < visibleCount; ++index)
                AppendPreviewItem(preview, ref appendedCount, FormatValue(list[index], depth));

            if (count <= previewCount)
                return preview.Append(']').ToString();

            AppendPreviewItem(preview, ref appendedCount, "...");
            AppendPreviewItem(preview, ref appendedCount, FormatValue(list[count - 1], depth));
            return preview.Append("] (n=").Append(count).Append(')').ToString();
        }

        static string FormatEnumerable(IEnumerable enumerable, int depth, Type elementType)
        {
            if (elementType == typeof(bool))
            {
                using var pooledBits = ConduitUtility.GetStringBuilder(out var bits);
                var count = 0;
                var previewCount = GetPreviewCount(elementType);
                var lastBit = '0';
                var bitScanLimitReached = false;
                foreach (var item in enumerable)
                {
                    if (count == MaxEnumerableScan)
                    {
                        bitScanLimitReached = true;
                        break;
                    }

                    lastBit = item is true ? '1' : '0';
                    if (count < previewCount)
                        bits.Append(lastBit);

                    count++;
                }

                if (count == 0)
                    return string.Empty;

                if (count <= previewCount)
                    return bits.ToString();

                bits.Length = previewCount - 1;
                if (bitScanLimitReached)
                {
                    bits.Append("... (n>").Append(MaxEnumerableScan).Append(')');
                    return bits.ToString();
                }

                bits.Append("...").Append(lastBit).Append(" (n=").Append(count).Append(')');
                return bits.ToString();
            }

            using var pooledPreview = ConduitUtility.GetStringBuilder(out var preview);
            preview.Append('[');
            var appendedCount = 0;
            object? lastItem = null;
            var itemCount = 0;
            var maxPreviewCount = GetPreviewCount(elementType);
            var itemScanLimitReached = false;
            foreach (var item in enumerable)
            {
                if (itemCount == MaxEnumerableScan)
                {
                    itemScanLimitReached = true;
                    break;
                }

                if (itemCount < maxPreviewCount - 1)
                    AppendPreviewItem(preview, ref appendedCount, FormatValue(item, depth));

                lastItem = item;
                ++itemCount;
            }

            if (itemCount == 0)
                return "[]";

            if (itemCount <= maxPreviewCount)
            {
                if (appendedCount < itemCount)
                    AppendPreviewItem(preview, ref appendedCount, FormatValue(lastItem, depth));

                return preview.Append(']').ToString();
            }

            AppendPreviewItem(preview, ref appendedCount, "...");
            if (itemScanLimitReached)
                return preview
                    .Append("] (n>")
                    .Append(MaxEnumerableScan)
                    .Append(')')
                    .ToString();

            AppendPreviewItem(preview, ref appendedCount, FormatValue(lastItem, depth));
            return preview.Append("] (n=").Append(itemCount).Append(')').ToString();
        }

        static void AppendPreviewItem(
            StringBuilder builder,
            ref int itemCount,
            string item)
        {
            if (itemCount++ > 0)
                builder.Append(", ");

            builder.Append(item);
        }

        static Type ResolveDeclaredType(Type rootType, string propertyPath)
        {
            var currentType = rootType;
            var path = propertyPath.AsSpan();
            var segmentStart = 0;
            while (segmentStart <= path.Length)
            {
                var separatorIndex = path[segmentStart..].IndexOf('.');
                var segment = separatorIndex < 0
                    ? path[segmentStart..]
                    : path.Slice(segmentStart, separatorIndex);

                if (segment.SequenceEqual("Array"))
                {
                    currentType = GetEnumerableElementType(currentType) ?? typeof(object);
                }
                else if (segment.StartsWith("data[", Ordinal))
                {
                    currentType = GetEnumerableElementType(currentType) ?? typeof(object);
                }
                else
                {
                    FieldInfo? field = null;
                    foreach (var candidate in GetInspectableFields(currentType))
                    {
                        if (!segment.Equals(candidate.Name, Ordinal))
                            continue;

                        field = candidate;
                        break;
                    }

                    if (field == null)
                        return typeof(object);

                    currentType = field.FieldType;
                }

                if (separatorIndex < 0)
                    break;

                segmentStart += separatorIndex + 1;
            }

            return currentType;
        }

        static Type GetEnumerableElementType(Type type)
            => enumerableElementTypeCache.GetOrAdd(type, static value =>
            {
                if (value.IsArray)
                    return value.GetElementType() ?? typeof(object);

                if (value.IsGenericType)
                {
                    var arguments = value.GetGenericArguments();
                    if (arguments.Length == 1)
                        return arguments[0];
                }

                foreach (var candidate in value.GetInterfaces())
                    if (candidate.IsGenericType
                        && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        return candidate.GetGenericArguments()[0];

                return typeof(object);
            });

        static int GetPreviewCount(Type elementType)
        {
            if (elementType == typeof(bool))
                return 512;

            if (elementType == typeof(byte) || elementType == typeof(sbyte))
                return 128;

            if (elementType == typeof(short) || elementType == typeof(ushort))
                return 64;

            if (elementType == typeof(int) || elementType == typeof(uint))
                return 32;

            if (elementType == typeof(long) || elementType == typeof(ulong))
                return 16;

            return 8;
        }

        static string FormatDictionary(IDictionary dictionary, int depth)
        {
            if (dictionary.Count == 0)
                return "{}";

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            if (dictionary.Count <= MaxCollectionPreview)
                builder.Append('{');
            else
                builder.Append("{count=").Append(dictionary.Count).Append("; first=");

            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count == MaxCollectionPreview)
                    break;

                if (count++ > 0)
                    builder.Append(", ");

                builder
                    .Append(FormatValue(entry.Key, depth))
                    .Append("=>")
                    .Append(FormatValue(entry.Value, depth));
            }

            return builder.Append('}').ToString();
        }

        static string? SummarizeObject(object value, int depth)
        {
            var fields = GetInspectableFields(value.GetType());
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append(value.GetType().Name).Append('{');
            var count = 0;
            foreach (var field in fields)
            {
                if (!IsUnitySerializableField(field))
                    continue;

                TryFormatFieldValue(field, value, depth, out var fieldValue);

                if (count++ > 0)
                    builder.Append(", ");

                builder.Append(field.Name).Append('=').Append(fieldValue);
                if (count >= MaxCollectionPreview)
                    break;
            }

            return count == 0 ? null : builder.Append('}').ToString();
        }

        static void AppendObjectIdentifiers(StringBuilder builder, Object target, int indent, bool includeGuid)
        {
            var assetPath = includeGuid && EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;
            AppendObjectIdentifiers(builder, target, indent, includeGuid, assetPath);
        }

        static void AppendObjectIdentifiers(
            StringBuilder builder,
            Object target,
            int indent,
            bool includeGuid,
            string assetPath)
        {
            builder.Append(' ', indent);
            builder.Append("ID: ").AppendLine(ConduitUtility.FormatObjectId(target));

            if (!includeGuid || assetPath.Length == 0)
                return;

            if (AssetDatabase.AssetPathToGUID(assetPath) is not { Length: > 0 } guid)
                return;

            builder.Append(' ', indent);
            builder.Append("GUID: ").AppendLine(guid);
        }

        static string DescribeObject(Object target)
        {
            if (target == null)
                return "null";

            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;
            return DescribeObject(target, assetPath);
        }

        static string DescribeObject(Object target, string assetPath)
        {
            if (target == null)
                return "null";

            return target switch
            {
                GameObject gameObject                        => FormatObjectDescription(nameof(GameObject), ConduitUtility.BuildHierarchyPath(gameObject.transform), assetPath),
                Component component                          => FormatObjectDescription(component.GetType().Name, ConduitUtility.BuildHierarchyPath(component.transform), assetPath),
                MonoScript when assetPath is { Length: > 0 } => $"Script({assetPath})",
                _ when assetPath is { Length: > 0 }          => FormatObjectDescription(target.GetType().Name, target.name, assetPath),
                _                                            => $"{target.GetType().Name}(\"{target.name}\")",
            };
        }

        static string FormatObjectDescription(string typeName, string identifier, string assetPath)
            => string.IsNullOrWhiteSpace(assetPath)
                ? $"{typeName}(\"{identifier}\")"
                : IsRedundantAssetIdentifier(identifier, assetPath)
                    ? $"{typeName}({assetPath})"
                    : $"{typeName}(\"{identifier}\", {assetPath})";

        static bool IsRedundantAssetIdentifier(string identifier, string assetPath)
            => string.Equals(identifier, Path.GetFileNameWithoutExtension(assetPath), Ordinal);

        static string FormatSceneName(Scene scene)
            => ConduitUtility.FormatScenePath(scene, "unsaved scene");

        static string FormatVector(float x, float y)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.AppendInvariant(x, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(y, "0.###");
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatVector(float x, float y, float z)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.AppendInvariant(x, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(y, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(z, "0.###");
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatVector(float x, float y, float z, float w)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.AppendInvariant(x, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(y, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(z, "0.###");
            builder.Append(", ");
            builder.AppendInvariant(w, "0.###");
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatString(string value)
        {
            if (value == null)
                return "null";

            var compact = TrimCompact(value);
            return $"\"{compact}\"";
        }

        static string TrimCompact(string value)
        {
            if (value is not { Length: > 0 })
                return string.Empty;

            var normalized = value
                .Replace("\r\n", "\\n")
                .Replace('\n', ' ')
                .Replace('\r', ' ')
                .Replace('\t', ' ')
                .Trim();

            return normalized.Length <= MaxStringLength
                ? normalized
                : $"{normalized[..MaxStringLength]}...";
        }

        static string FormatUnavailable(Exception exception)
        {
            var type = ConduitUtility.SimplifyTypeName(exception.GetType().FullName ?? exception.GetType().Name);
            var message = TrimCompact(exception.Message);
            return string.IsNullOrWhiteSpace(message)
                ? $"<unavailable: {type}>"
                : $"<unavailable: {type}: {message}>";
        }

        readonly struct IndexableAccess
        {
            public static readonly IndexableAccess Unavailable = new(false, typeof(object), null, null);

            public IndexableAccess(
                bool available,
                Type elementType,
                Func<object, int>? getLength,
                Func<object, int, object?>? getElement
            )
            {
                Available = available;
                ElementType = elementType;
                GetLength = getLength;
                GetElement = getElement;
            }

            public bool Available { get; }
            public Type ElementType { get; }
            public Func<object, int>? GetLength { get; }
            public Func<object, int, object?>? GetElement { get; }
        }

        readonly struct CustomShowAccess
        {
            public CustomShowAccess(MethodInfo? method) => Method = method;

            public MethodInfo? Method { get; }
        }
    }
}
