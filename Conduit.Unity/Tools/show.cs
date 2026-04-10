#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
        static readonly ConcurrentDictionary<Type, FieldInfo[]> fieldCache = new();

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
            => ConduitSearchUtility.Resolve(query) switch
            {
                { Count: 0 }           => $"No matches for '{query}'.",
                { Count: 1 } matches   => DebugResolvedObject(matches[0]),
                { Count: > 1 } matches => ConduitSearchUtility.FormatMatches(matches, includeHint: true),
            };

        static string DebugResolvedObject(ResolvedObjectMatch match)
        {
            var target = match.Target;
            if (TryGetCustomShowText(target, out var customShowText))
                return customShowText;

            if (target is EditorWindow window)
                return DebugEditorWindow(window);

            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;

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
                GameObject gameObject => DebugExactGameObject(gameObject),
                _                     => DebugLooseObject(target),
            };
        }

        static bool TryGetCustomShowText(Object target, out string customShowText)
        {
            customShowText = string.Empty;
            if (target == null)
                return false;

            var method = target.GetType().GetMethod(
                "ToStringForMCP",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null
            );
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
            builder.AppendLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Position: x={0:0.###}, y={1:0.###}, width={2:0.###}, height={3:0.###}",
                    position.x,
                    position.y,
                    position.width,
                    position.height
                )
            );
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
            builder.AppendLine($"Main Object: {DescribeObject(sceneAsset)}");
            if (sceneAsset != null)
                AppendObjectIdentifiers(builder, sceneAsset, 0, includeGuid: true);

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
                builder.AppendLine();
                builder.AppendLine("Hierarchy:");
                AppendHierarchy(builder, root.transform, includeSiblings: false);
                builder.AppendLine();

                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    AppendGameObject(builder, transform.gameObject);

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
                        AppendAssetObject(builder, subasset, "Subasset");
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
            builder.AppendLine($"Main Object: {DescribeObject(mainAsset)}");
            var subassetCount = Math.Max(0, allAssets.Count - 1);
            if (subassetCount > 0)
                builder.AppendLine($"Imported Subassets: {subassetCount}");

            builder.AppendLine();

            if (mainAsset != null)
                AppendAssetObject(builder, mainAsset, "Main Object");

            foreach (var subasset in allAssets)
                if (subasset != mainAsset)
                    AppendAssetObject(builder, subasset, "Subasset");

            return builder.TrimEnd().ToString();
        }

        static string DebugExactGameObject(GameObject gameObject)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            var assetPath = EditorUtility.IsPersistent(gameObject)
                ? AssetDatabase.GetAssetPath(gameObject)
                : string.Empty;

            builder.AppendLine($"Object: {DescribeObject(gameObject)}");
            builder.AppendLine(!string.IsNullOrWhiteSpace(assetPath) ? $"Asset: {assetPath}" : $"Scene: {FormatSceneName(gameObject.scene)}");
            builder.AppendLine();
            builder.AppendLine("Hierarchy:");
            AppendHierarchy(builder, gameObject.transform, includeSiblings: false);
            builder.AppendLine();

            foreach (var transform in gameObject.GetComponentsInChildren<Transform>(true))
                AppendGameObject(builder, transform.gameObject);

            return builder.TrimEnd().ToString();
        }

        static string DebugLooseObject(Object target)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(assetPath))
                builder.AppendLine($"Asset: {assetPath}");
            else if (target is Component component)
                builder.AppendLine($"Scene: {FormatSceneName(component.gameObject.scene)}");

            builder.AppendLine($"Object: {DescribeObject(target)}");
            AppendObjectIdentifiers(builder, target, 0, includeGuid: true);
            AppendSerializableFields(builder, target, 2);
            AppendNonSerializableFields(builder, target, 2);
            return builder.TrimEnd().ToString();
        }

        static void AppendHierarchy(StringBuilder builder, Transform transform, bool includeSiblings)
        {
            if (!includeSiblings)
            {
                AppendHierarchyNode(builder, transform, string.Empty, true);
                return;
            }

            var roots = transform.gameObject.scene.GetRootGameObjects();
            for (var index = 0; index < roots.Length; index++)
                AppendHierarchyNode(builder, roots[index].transform, string.Empty, index == roots.Length - 1);
        }

        static void AppendHierarchyNode(StringBuilder builder, Transform transform, string prefix, bool isLast)
        {
            builder.Append(prefix);
            builder.Append(isLast ? "└───" : "├───");
            builder.AppendLine(transform.name);

            var childPrefix = prefix + (isLast ? "    " : "│   ");
            for (var index = 0; index < transform.childCount; index++)
                AppendHierarchyNode(builder, transform.GetChild(index), childPrefix, index == transform.childCount - 1);
        }

        static string DebugScene(Scene scene)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine($"Scene: {FormatSceneName(scene)}");
            builder.AppendLine();

            var componentIdentifiers = BuildSceneComponentIdentifiers(scene);
            if (componentIdentifiers.Count > 0)
            {
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
                    builder.AppendLine($"- {entry.Value} = {entry.Key.Name}");

                builder.AppendLine();
            }

            builder.AppendLine("Hierarchy:");
            var roots = scene.GetRootGameObjects();
            for (var index = 0; index < roots.Length; index++)
                AppendSceneHierarchy(builder, roots[index].transform, string.Empty, index == roots.Length - 1, componentIdentifiers);

            return builder.TrimEnd().ToString();
        }

        static Scene TryGetLoadedScene(string assetPath)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid())
                    if (scene.isLoaded)
                        if (string.Equals(scene.path, assetPath, OrdinalIgnoreCase))
                            return scene;
            }

            return default;
        }

        static Dictionary<Type, string> BuildSceneComponentIdentifiers(Scene scene)
        {
            using var pooledTypes = ConduitUtility.GetPooledList<Type>(out var types);
            using var pooledSeenTypes = ConduitUtility.GetPooledSet<Type>(out var seenTypes);
            foreach (var root in scene.GetRootGameObjects())
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            foreach (var component in transform.GetComponents<Component>())
            {
                if (component is null or Transform or RectTransform)
                    continue;

                var componentType = component.GetType();
                if (seenTypes.Add(componentType))
                    types.Add(componentType);
            }

            types.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

            var identifiers = new Dictionary<Type, string>();
            using var pooledUsed = ConduitUtility.GetPooledSet<string>(out var used);
            foreach (var type in types)
            {
                var identifier = CreateComponentIdentifier(type.Name, used);
                identifiers.Add(type, identifier);
                used.Add(identifier);
            }

            return identifiers;
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

            var initials = new StringBuilder();
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

        static void AppendSceneHierarchy(StringBuilder builder, Transform transform, string prefix, bool isLast, IReadOnlyDictionary<Type, string> componentIdentifiers)
        {
            var gameObject = transform.gameObject;
            builder.Append(prefix);
            builder.Append(isLast ? "└───" : "├───");
            builder.Append(gameObject.name);
            builder.Append(" [");
            if (!gameObject.activeInHierarchy)
                builder.Append("inactive | ");

            builder.Append(ConduitUtility.FormatObjectId(gameObject));

            foreach (var componentId in GetSceneComponentIdentifiers(gameObject, componentIdentifiers))
            {
                builder.Append(" | ");
                builder.Append(componentId);
            }

            builder.AppendLine("]");

            var childPrefix = prefix + (isLast ? "    " : "│   ");
            for (var index = 0; index < transform.childCount; index++)
                AppendSceneHierarchy(builder, transform.GetChild(index), childPrefix, index == transform.childCount - 1, componentIdentifiers);
        }

        static IEnumerable<string> GetSceneComponentIdentifiers(GameObject gameObject, IReadOnlyDictionary<Type, string> componentIdentifiers)
        {
            foreach (var component in gameObject.GetComponents<Component>())
                if (component is not (null or Transform or RectTransform))
                    if (componentIdentifiers.TryGetValue(component.GetType(), out var identifier))
                        yield return identifier;
        }

        static void AppendGameObject(StringBuilder builder, GameObject gameObject)
        {
            builder.AppendLine($"GameObject: {ConduitUtility.BuildHierarchyPath(gameObject.transform)} [{ConduitUtility.FormatObjectId(gameObject)}]");

            if (gameObject.GetComponents<Component>() is not { Length: > 0 } components)
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

                builder.AppendLine($"  - {component.GetType().FullName} [{ConduitUtility.FormatObjectId(component)}]");
                AppendSerializableFields(builder, component, 4);
                AppendNonSerializableFields(builder, component, 4);
            }

            builder.AppendLine();
        }

        static void AppendAssetObject(StringBuilder builder, Object assetObject, string label)
        {
            builder.AppendLine($"{label}: {DescribeObject(assetObject)}");
            AppendObjectIdentifiers(builder, assetObject, 2, includeGuid: true);
            AppendSerializableFields(builder, assetObject, 2);
            AppendNonSerializableFields(builder, assetObject, 2);
            builder.AppendLine();
        }

        static void AppendSerializableFields(StringBuilder builder, Object target, int indent)
        {
            try
            {
                var serializedObject = new SerializedObject(target);

                if (GetTopLevelProperties(serializedObject) is not { Length: > 0 } properties)
                    return;

                builder.Append(' ', indent);
                builder.AppendLine("Serializable:");
                foreach (var property in properties)
                    if (property.propertyPath != "m_ObjectHideFlags")
                        AppendSerializedProperty(builder, target, property, indent + 2);
            }
            catch (Exception exception)
            {
                builder.Append(' ', indent);
                builder.AppendLine("Serializable:");
                builder.Append(' ', indent + 2);
                builder.AppendLine($"- <unavailable: {exception.Message}>");
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

                var valueText = TryGetFieldValue(field, target, out var value)
                    ? FormatValue(value, 0)
                    : "<unavailable>";

                builder.Append(' ', indent + 2);
                builder.AppendLine($"- {field.Name}: {valueText}");
            }
        }

        static SerializedProperty[] GetTopLevelProperties(SerializedObject serializedObject)
        {
            using var pooledProperties = ConduitUtility.GetPooledList<SerializedProperty>(out var properties);
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.depth != 0)
                    continue;

                properties.Add(iterator.Copy());
            }

            return properties.ToArray();
        }

        static void AppendSerializedProperty(StringBuilder builder, Object target, SerializedProperty property, int indent)
        {
            if (property is { isArray: true, propertyType: not SerializedPropertyType.String })
            {
                builder.Append(' ', indent);
                builder.AppendLine($"- {property.propertyPath}: {FormatArrayProperty(target, property)}");
                return;
            }

            if (property is { hasVisibleChildren: true, propertyType: SerializedPropertyType.Generic })
            {
                builder.Append(' ', indent);
                builder.AppendLine($"- {property.propertyPath}:");
                foreach (var child in GetImmediateChildren(property))
                    AppendSerializedProperty(builder, target, child, indent + 2);

                return;
            }

            builder.Append(' ', indent);
            builder.AppendLine($"- {property.propertyPath}: {FormatSerializedValue(property)}");
        }

        static SerializedProperty[] GetImmediateChildren(SerializedProperty property)
        {
            using var pooledChildren = ConduitUtility.GetPooledList<SerializedProperty>(out var children);
            var cursor = property.Copy();
            var end = cursor.GetEndProperty();
            var enterChildren = true;
            while (cursor.NextVisible(enterChildren) && !SerializedProperty.EqualContents(cursor, end))
            {
                enterChildren = false;
                if (cursor.depth == property.depth + 1)
                    children.Add(cursor.Copy());
            }

            return children.ToArray();
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
                var bits = new StringBuilder();
                for (var index = 0; index < count; index++)
                {
                    var bit = FormatSerializedElement(target, property.GetArrayElementAtIndex(index), 1) == "true" ? '1' : '0';
                    if (count > previewCount && index >= previewCount - 1 && index < count - 1)
                        continue;

                    bits.Append(bit);
                }

                return count > previewCount
                    ? $"{bits.ToString().Insert(previewCount - 1, "...")} (n={count})"
                    : bits.ToString();
            }

            using var pooledPreview = ConduitUtility.GetPooledList<string>(out var preview);
            var last = string.Empty;
            for (var index = 0; index < count; index++)
            {
                var formatted = FormatSerializedElement(target, property.GetArrayElementAtIndex(index), 1);
                if (count <= previewCount || preview.Count < previewCount - 1)
                    preview.Add(formatted);

                last = formatted;
            }

            if (count <= previewCount)
                return $"[{string.Join(", ", preview)}]";

            preview.Add("...");
            preview.Add(last);
            return $"[{string.Join(", ", preview)}] (n={count})";
        }

        static string FormatSerializedElement(Object target, SerializedProperty property, int depth)
        {
            if (depth > 1)
                return $"<{property.propertyType}>";

            if (property is { isArray: true, propertyType: not SerializedPropertyType.String })
                return FormatArrayProperty(target, property);

            if (property is { hasVisibleChildren: true, propertyType: SerializedPropertyType.Generic })
            {
                if (GetImmediateChildren(property) is not { Length: > 0 } children)
                    return "{}";

                var builder = new StringBuilder();
                builder.Append('{');
                var previewCount = Math.Min(children.Length, MaxCollectionPreview);
                for (var index = 0; index < previewCount; index++)
                {
                    if (index > 0)
                        builder.Append(", ");

                    var child = children[index];
                    builder.Append(child.name);
                    builder.Append('=');
                    builder.Append(FormatSerializedElement(target, child, depth + 1));
                }

                if (children.Length > MaxCollectionPreview)
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

        static bool TryGetFieldValue(FieldInfo field, object target, out object? value)
        {
            try
            {
                value = field.GetValue(target);
                return true;
            }
            catch
            {
                value = null;
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

        static string FormatEnumerable(IEnumerable enumerable, int depth, Type elementType)
        {
            if (elementType == typeof(bool))
            {
                var bits = new List<char>();
                var count = 0;
                var previewCount = GetPreviewCount(elementType);
                var lastBit = '0';
                foreach (var item in enumerable)
                {
                    lastBit = item is true ? '1' : '0';
                    if (count < previewCount)
                        bits.Add(lastBit);

                    count++;
                }

                if (count == 0)
                    return string.Empty;

                if (count <= previewCount)
                    return new(bits.ToArray());

                var preview = new char[previewCount - 1];
                for (var index = 0; index < preview.Length; index++)
                    preview[index] = bits[index];

                return $"{new string(preview)}...{lastBit} (n={count})";
            }

            var previewItems = new List<string>();
            var lastItem = string.Empty;
            var itemCount = 0;
            var maxPreviewCount = GetPreviewCount(elementType);
            foreach (var item in enumerable)
            {
                var formatted = FormatValue(item, depth);
                if (itemCount < maxPreviewCount - 1)
                    previewItems.Add(formatted);

                lastItem = formatted;
                itemCount++;
            }

            if (itemCount == 0)
                return "[]";

            if (itemCount <= maxPreviewCount)
            {
                if (previewItems.Count < itemCount)
                    previewItems.Add(lastItem);

                return $"[{string.Join(", ", previewItems)}]";
            }

            previewItems.Add("...");
            previewItems.Add(lastItem);
            return $"[{string.Join(", ", previewItems)}] (n={itemCount})";
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
        {
            if (type == null)
                return typeof(object);

            if (type.IsArray)
                return type.GetElementType() ?? typeof(object);

            if (type.IsGenericType && type.GetGenericArguments().Length == 1)
                return type.GetGenericArguments()[0];

            foreach (var candidate in type.GetInterfaces())
                if (candidate.IsGenericType)
                    if (candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        return candidate.GetGenericArguments()[0];

            return typeof(object);
        }

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

            var preview = new List<string>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (preview.Count >= MaxCollectionPreview)
                    break;

                preview.Add($"{FormatValue(entry.Key, depth)}=>{FormatValue(entry.Value, depth)}");
            }

            return dictionary.Count <= MaxCollectionPreview
                ? $"{{{string.Join(", ", preview)}}}"
                : $"{{count={dictionary.Count}; first={string.Join(", ", preview)}}}";
        }

        static string? SummarizeObject(object value, int depth)
        {
            var fields = GetInspectableFields(value.GetType());
            using var pooledParts = ConduitUtility.GetPooledList<string>(out var parts);
            foreach (var field in fields)
            {
                if (!IsUnitySerializableField(field))
                    continue;

                var fieldValue = TryGetFieldValue(field, value, out var resolvedValue)
                    ? FormatValue(resolvedValue, depth)
                    : "<unavailable>";

                parts.Add($"{field.Name}={fieldValue}");
                if (parts.Count >= MaxCollectionPreview)
                    break;
            }

            return parts.Count == 0
                ? null
                : $"{value.GetType().Name}{{{string.Join(", ", parts)}}}";
        }

        static void AppendObjectIdentifiers(StringBuilder builder, Object target, int indent, bool includeGuid)
        {
            builder.Append(' ', indent);
            builder.AppendLine($"ID: {ConduitUtility.FormatObjectId(target)}");

            if (!includeGuid)
                return;

            if (!EditorUtility.IsPersistent(target) || AssetDatabase.GetAssetPath(target) is not { Length: > 0 } assetPath)
                return;

            if (AssetDatabase.AssetPathToGUID(assetPath) is not { Length: > 0 } guid)
                return;

            builder.Append(' ', indent);
            builder.AppendLine($"GUID: {guid}");
        }

        static string DescribeObject(Object target)
        {
            if (target == null)
                return "null";

            var assetPath = EditorUtility.IsPersistent(target)
                ? AssetDatabase.GetAssetPath(target)
                : string.Empty;

            return target switch
            {
                GameObject gameObject                        => FormatObjectDescription(nameof(GameObject), ConduitUtility.BuildHierarchyPath(gameObject.transform), assetPath),
                Component component                          => FormatObjectDescription(component.GetType().Name, ConduitUtility.BuildHierarchyPath(component.transform), assetPath),
                MonoScript when assetPath is { Length: > 0 } => $"Script({assetPath})",
                _ when assetPath is { Length: > 0 }          => $"{target.GetType().Name}(\"{target.name}\", {assetPath})",
                _                                            => $"{target.GetType().Name}(\"{target.name}\")",
            };
        }

        static string FormatObjectDescription(string typeName, string identifier, string assetPath)
            => string.IsNullOrWhiteSpace(assetPath)
                ? $"{typeName}(\"{identifier}\")"
                : $"{typeName}(\"{identifier}\", {assetPath})";

        static string FormatSceneName(Scene scene)
            => ConduitUtility.FormatScenePath(scene, "unsaved scene");

        static string FormatVector(float x, float y)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatVector(float x, float y, float z)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(z.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatVector(float x, float y, float z, float w)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append('(');
            builder.Append(x.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(y.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(z.ToString("0.###", CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(w.ToString("0.###", CultureInfo.InvariantCulture));
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
    }
}
