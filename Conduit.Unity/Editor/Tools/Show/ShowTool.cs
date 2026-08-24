#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using static System.StringComparison;
using Object = UnityEngine.Object;

namespace Conduit
{
    static partial class ShowTool
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

        internal static string Show(string query)
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
            var target = match.RequireTarget();
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
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            var position = window.position;
            builder.AppendLine($"Editor Window: {ConduitSearchUtility.GetEditorWindowDisplayName(window)}");
            builder.AppendLine($"Type: {window.GetType().FullName}");
            builder.AppendLine($"Title: {ConduitSearchUtility.GetEditorWindowTitle(window)}");
            builder.AppendLine($"Object: {ConduitObjectId.FormatObjectId(window)}");
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
            return builder.ToTrimmedString();
        }

        static string DebugSceneAsset(string assetPath)
        {
            var loadedScene = TryGetLoadedScene(assetPath);
            if (loadedScene.IsValid())
                if (loadedScene.isLoaded)
                    return DebugScene(loadedScene);

            var sceneAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine($"Asset: {assetPath}");
            builder.AppendLine($"Main Object: {DescribeObject(sceneAsset, assetPath)}");
            if (sceneAsset != null)
                AppendObjectIdentifiers(builder, sceneAsset, 0, includeGuid: true, assetPath: assetPath);

            builder.AppendLine("Cannot inspect the hierarchy because the scene is closed.");
            return builder.ToTrimmedString();
        }

        static string DebugPrefab(string assetPath)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                builder.AppendLine($"Asset: {assetPath}");
                builder.AppendLine($"Main Object: {DescribeObject(root)}");
                builder.AppendLine("Object IDs are omitted because prefab contents are temporary.");
                builder.AppendLine();
                AppendGameObjectHierarchyDetails(builder, root, includeObjectIds: false);

                using var pooledSubassets = ConduitPool.GetPooledList<Object>(out var subassets);
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

                return builder.ToTrimmedString();
            }
            finally
            {
                if (root != null)
                    PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static string DebugAsset(string assetPath)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            using var pooledAssets = ConduitPool.GetPooledList<Object>(out var allAssets);
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

            return builder.ToTrimmedString();
        }

        static string DebugExactGameObject(GameObject gameObject, string assetPath)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine($"Object: {DescribeObject(gameObject, assetPath)}");
            builder.AppendLine(!string.IsNullOrWhiteSpace(assetPath) ? $"Asset: {assetPath}" : $"Scene: {FormatSceneName(gameObject.scene)}");
            builder.AppendLine();
            AppendGameObjectHierarchyDetails(builder, gameObject);

            return builder.ToTrimmedString();
        }

        static string DebugLooseObject(Object target, string assetPath)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            if (!string.IsNullOrWhiteSpace(assetPath))
                builder.AppendLine($"Asset: {assetPath}");
            else if (target is Component component)
                builder.AppendLine($"Scene: {FormatSceneName(component.gameObject.scene)}");

            builder.AppendLine($"Object: {DescribeObject(target, assetPath)}");
            AppendObjectIdentifiers(builder, target, 0, includeGuid: true, assetPath: assetPath);
            AppendSerializableFields(builder, target, 2);
            AppendNonSerializableFields(builder, target, 2);
            return builder.ToTrimmedString();
        }

        readonly struct IndexableAccess
        {
            internal static readonly IndexableAccess Unavailable = new(
                false,
                typeof(object),
                null,
                null
            );

            internal IndexableAccess(
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

            internal bool Available { get; }
            internal Type ElementType { get; }
            internal Func<object, int>? GetLength { get; }
            internal Func<object, int, object?>? GetElement { get; }
        }

        readonly struct CustomShowAccess
        {
            internal CustomShowAccess(MethodInfo? method) => Method = method;

            internal MethodInfo? Method { get; }
        }
    }
}
