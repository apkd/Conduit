#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class BuiltInProjectSettingsProviders
    {
        const string ConduitDevelopmentDefine = "CONDUIT_INCLUDE_IN_DEBUG_BUILDS";
        static readonly Regex qualityElementPattern = new(
            @"^m_QualitySettings\.Array\.data\[(?<index>\d+)\](?:\.(?<field>.+))?$",
            RegexOptions.CultureInvariant
        );
        static readonly Regex qualityPlatformPattern = new(
            @"^m_PerPlatformDefaultQuality\.Array\.data\[(?<index>\d+)\](?:\.(?<field>.+))?$",
            RegexOptions.CultureInvariant
        );
        static readonly Regex serializedMapElementPattern = new(
            @"^(?<map>[^.]+)\.Array\.data\[(?<index>\d+)\](?:\.(?<field>.+))?$",
            RegexOptions.CultureInvariant
        );
        static readonly (string Prefix, string Platform)[] playerPlatformPrefixes =
        {
            ("android_", "android"),
            ("i_phone_", "ios"),
            ("i_os_", "ios"),
            ("tv_os_", "tvos"),
            ("vision_os_", "visionos"),
            ("web_gl_", "webgl"),
            ("wsa_", "windows_store_apps"),
            ("metro_", "windows_store_apps"),
            ("xbox_one_", "xbox_one"),
            ("ps4_", "ps4"),
            ("ps5_", "ps5"),
            ("switch_", "switch"),
        };

        // stable serialized files cover editor versions without binding to Unity's changing internal APIs.
        static readonly (string Prefix, string Path)[] projectSettingsAssets =
        {
            ("player_settings", "ProjectSettings/ProjectSettings.asset"),
            ("audio_settings", "ProjectSettings/AudioManager.asset"),
            ("physics_settings", "ProjectSettings/DynamicsManager.asset"),
            ("editor_settings", "ProjectSettings/EditorSettings.asset"),
            ("graphics_settings", "ProjectSettings/GraphicsSettings.asset"),
            ("memory_settings", "ProjectSettings/MemorySettings.asset"),
            ("multiplayer_settings", "ProjectSettings/MultiplayerManager.asset"),
            ("navigation_settings", "ProjectSettings/NavMeshAreas.asset"),
            ("physics_2d_settings", "ProjectSettings/Physics2DSettings.asset"),
            ("preset_manager", "ProjectSettings/PresetManager.asset"),
            ("tag_manager", "ProjectSettings/TagManager.asset"),
            ("time_settings", "ProjectSettings/TimeManager.asset"),
            ("version_control_settings", "ProjectSettings/VersionControlSettings.asset"),
            ("vfx_settings", "ProjectSettings/VFXManager.asset"),
        };

        [ConduitProjectSettingsProvider]
        static void RegisterProjectSettingsAssets(ProjectSettingsRegistry registry)
        {
            var playerSettingsMaps = ReadSerializedMapKeys("ProjectSettings/ProjectSettings.asset");
            foreach (var asset in projectSettingsAssets)
                SerializedProjectSettingsProvider.RegisterFile(
                    registry,
                    asset.Prefix,
                    asset.Path,
                    asset.Prefix switch
                    {
                        "player_settings"    => path => MapPlayerSettingsPath(path, playerSettingsMaps),
                        "tag_manager"       => MapTagManagerPath,
                        "graphics_settings" => MapGraphicsSettingsPath,
                        _                   => null,
                    }
                );
        }

        static string? MapPlayerSettingsPath(
            string propertyPath,
            IReadOnlyDictionary<string, List<string>> maps)
        {
            if (maps.ContainsKey(propertyPath))
                return null;

            var match = serializedMapElementPattern.Match(propertyPath);
            if (!match.Success || !maps.TryGetValue(match.Groups["map"].Value, out var keys))
                return RelocatePlayerSetting(SerializedProjectSettingsProvider.ToKey(propertyPath));

            int index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            string field = match.Groups["field"].Value;
            if (index >= keys.Count || !field.StartsWith("second", StringComparison.Ordinal))
                return null;

            string setting = SerializedProjectSettingsProvider.ToKey(match.Groups["map"].Value);
            if (setting.StartsWith("build_target_", StringComparison.Ordinal))
                setting = setting["build_target_".Length..];
            if (setting.EndsWith("_per_platform", StringComparison.Ordinal))
                setting = setting[..^"_per_platform".Length];

            string suffix = string.Empty;
            if (field.Length > "second".Length)
            {
                string nested = SerializedProjectSettingsProvider.ToKey(
                    "value." + field[("second".Length + 1)..]
                );
                suffix = nested["value".Length..];
            }
            return $"platforms.{keys[index]}.{setting}{suffix}";
        }

        static string RelocatePlayerSetting(string key)
        {
            foreach ((string prefix, string platform) in playerPlatformPrefixes)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    return $"platforms.{platform}.{key[prefix.Length..]}";

            return key;
        }

        static Dictionary<string, List<string>> ReadSerializedMapKeys(string path)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (SerializedProjectSettingsProvider.LoadSettingsAsset(path) is not { } target)
                return result;

            using var serializedObject = new SerializedObject(target);
            var iterator = serializedObject.GetIterator();
            if (!iterator.NextVisible(true))
                return result;

            do
            {
                if (!iterator.isArray || iterator.propertyType == SerializedPropertyType.String)
                    continue;

                var keys = new List<string>(iterator.arraySize);
                for (int index = 0, count = iterator.arraySize; index < count; ++index)
                {
                    var element = iterator.GetArrayElementAtIndex(index);
                    var first = element.FindPropertyRelative("first");
                    var second = element.FindPropertyRelative("second");
                    if (first == null || second == null)
                    {
                        keys.Clear();
                        break;
                    }

                    string value = first.propertyType switch
                    {
                        SerializedPropertyType.String => first.stringValue,
                        SerializedPropertyType.Enum when first.enumValueIndex >= 0
                                                         && first.enumValueIndex < first.enumNames.Length
                            => first.enumNames[first.enumValueIndex],
                        _ => first.intValue.ToString(CultureInfo.InvariantCulture),
                    };
                    keys.Add(PlatformKey(value));
                }

                if (keys.Count > 0)
                    result[iterator.propertyPath] = keys;
            }
            while (iterator.NextVisible(false));

            return result;
        }

        static string? MapGraphicsSettingsPath(string propertyPath)
            => propertyPath switch
            {
                "m_LogWhenShaderIsCompiled" => "log_shader_compilation",
                "m_CustomRenderPipeline"    => "default_render_pipeline",
                _                           => SerializedProjectSettingsProvider.ToKey(propertyPath),
            };

        [ConduitProjectSettingsProvider]
        static void RegisterQualitySettings(ProjectSettingsRegistry registry)
        {
            const string path = "ProjectSettings/QualitySettings.asset";
            if (!File.Exists(path))
                return;

            var catalog = ReadQualityCatalog(path);
            SerializedProjectSettingsProvider.RegisterFile(
                registry,
                "quality_settings",
                path,
                propertyPath => MapQualityPath(propertyPath, catalog)
            );
            RegisterQualitySelectors(registry, path, catalog);
        }

        static string? MapQualityPath(
            string propertyPath,
            (List<string> Levels, List<string> Platforms) catalog)
        {
            if (propertyPath == "m_QualitySettings")
                return "quality_levels";
            if (propertyPath == "m_CurrentQuality")
                return "current_level_index";
            if (propertyPath == "m_PerPlatformDefaultQuality")
                return "platforms";
            var platformMatch = qualityPlatformPattern.Match(propertyPath);
            if (platformMatch.Success)
            {
                int platformIndex = int.Parse(platformMatch.Groups["index"].Value, CultureInfo.InvariantCulture);
                if (platformIndex >= catalog.Platforms.Count
                    || platformMatch.Groups["field"].Value != "second")
                    return null;
                return $"platforms.{catalog.Platforms[platformIndex]}.default_level_index";
            }

            var match = qualityElementPattern.Match(propertyPath);
            if (!match.Success)
                return SerializedProjectSettingsProvider.ToKey(propertyPath);

            int index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            if (index >= catalog.Levels.Count)
                return null;

            string field = match.Groups["field"].Value;
            if (field.Length == 0)
                return $"quality_levels.{catalog.Levels[index]}";
            string mappedField = SerializedProjectSettingsProvider.ToKey(field) switch
            {
                "async_upload_time_slice"        => "async_asset_upload.time_slice",
                "async_upload_buffer_size"       => "async_asset_upload.buffer_size",
                "async_upload_persistent_buffer" => "async_asset_upload.persistent_buffer",
                "streaming_mipmaps_active"       => "lightmap_streaming.enabled",
                "streaming_mipmaps_add_all_cameras" => "lightmap_streaming.add_all_cameras",
                "streaming_mipmaps_memory_budget" => "lightmap_streaming.memory_budget",
                "streaming_mipmaps_renderers_per_frame" => "lightmap_streaming.renderers_per_frame",
                "streaming_mipmaps_max_level_reduction" => "lightmap_streaming.max_level_reduction",
                "streaming_mipmaps_max_file_iorequests" => "lightmap_streaming.max_file_io_requests",
                var other => other,
            };
            return $"quality_levels.{catalog.Levels[index]}.{mappedField}";
        }

        static void RegisterQualitySelectors(
            ProjectSettingsRegistry registry,
            string path,
            (List<string> Levels, List<string> Platforms) catalog)
        {
            registry.Add(
                "quality_settings.current_level",
                () => FormatLevel(SerializedProjectSettingsProvider.ReadFile(path, "m_CurrentQuality")),
                value => SerializedProjectSettingsProvider.WriteFile(path, "m_CurrentQuality", ParseLevel(value))
            );

            for (int index = 0, count = catalog.Platforms.Count; index < count; ++index)
            {
                string propertyPath = $"m_PerPlatformDefaultQuality.Array.data[{index}].second";
                registry.Add(
                    $"quality_settings.platforms.{catalog.Platforms[index]}.default_level",
                    () => FormatLevel(SerializedProjectSettingsProvider.ReadFile(path, propertyPath)),
                    value => SerializedProjectSettingsProvider.WriteFile(path, propertyPath, ParseLevel(value))
                );
            }

            string FormatLevel(string indexText)
            {
                int index = int.Parse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return index >= 0 && index < catalog.Levels.Count
                    ? catalog.Levels[index]
                    : indexText;
            }

            string ParseLevel(string value)
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    if (index >= 0 && index < catalog.Levels.Count)
                        return index.ToString(CultureInfo.InvariantCulture);
                    throw new FormatException($"Quality level index {index} is outside 0..{catalog.Levels.Count - 1}.");
                }

                string canonical = ProjectSettingKey.Canonicalize(value);
                index = catalog.Levels.FindIndex(level => level == canonical);
                if (index < 0)
                    throw new FormatException(
                        $"Unknown quality level '{value}'. Expected one of: {string.Join(", ", catalog.Levels)}."
                    );
                return index.ToString(CultureInfo.InvariantCulture);
            }
        }

        static (List<string> Levels, List<string> Platforms) ReadQualityCatalog(string path)
        {
            if (SerializedProjectSettingsProvider.LoadSettingsAsset(path) is not { } target)
                return (new(), new());

            using var serializedObject = new SerializedObject(target);
            var levels = serializedObject.FindProperty("m_QualitySettings");
            var names = new List<string>(levels?.arraySize ?? 0);
            if (levels != null)
                for (int index = 0, count = levels.arraySize; index < count; ++index)
                {
                    string? name = levels.GetArrayElementAtIndex(index)
                        .FindPropertyRelative("name")?.stringValue;
                    names.Add(ProjectSettingKey.Canonicalize(
                        string.IsNullOrWhiteSpace(name)
                            ? index.ToString(CultureInfo.InvariantCulture)
                            : name!
                    ));
                }

            var platforms = new List<string>();
            var platformSettings = serializedObject.FindProperty("m_PerPlatformDefaultQuality");
            if (platformSettings != null)
                for (int index = 0, count = platformSettings.arraySize; index < count; ++index)
                {
                    string? name = platformSettings.GetArrayElementAtIndex(index)
                        .FindPropertyRelative("first")?.stringValue;
                    platforms.Add(PlatformKey(
                        string.IsNullOrWhiteSpace(name)
                            ? index.ToString(CultureInfo.InvariantCulture)
                            : name!
                    ));
                }

            return (names, platforms);
        }

        static string? MapTagManagerPath(string propertyPath)
        {
            if (propertyPath == "layers")
                return null;
            if (propertyPath.StartsWith("tags.Array.data[", StringComparison.Ordinal))
                return "tags." + ArrayIndex(propertyPath);
            if (propertyPath.StartsWith("layers.Array.data[", StringComparison.Ordinal))
                return "layers." + ArrayIndex(propertyPath);
            if (propertyPath.StartsWith("m_SortingLayers.Array.data[", StringComparison.Ordinal))
                return "sorting_layers." + SerializedProjectSettingsProvider.ToKey(
                    propertyPath["m_SortingLayers.Array.data[".Length..]
                );

            return SerializedProjectSettingsProvider.ToKey(propertyPath);

            static string ArrayIndex(string path)
                => path[(path.LastIndexOf('[', path.Length - 1) + 1)..path.LastIndexOf(']')];
        }

        [ConduitProjectSettingsProvider]
        static void RegisterBuildSettings(ProjectSettingsRegistry registry)
        {
            registry.Add(
                "build_settings.active_platform",
                () => EditorUserBuildSettings.activeBuildTarget,
                target =>
                {
                    if (target == EditorUserBuildSettings.activeBuildTarget)
                        return;

                    var group = BuildPipeline.GetBuildTargetGroup(target);
                    if (group == BuildTargetGroup.Unknown)
                        throw new InvalidOperationException($"Build target '{target}' has no build target group.");
                    if (!BuildPipeline.IsBuildTargetSupported(group, target))
                        throw new InvalidOperationException(
                            $"Build target '{target}' is not installed or supported by this Unity Editor."
                        );
                    if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                        throw new InvalidOperationException($"Unity declined to switch to build target '{target}'.");
                }
            );

            registry.Add("build_settings.scenes.count", () => EditorBuildSettings.scenes.Length);
            var scenes = EditorBuildSettings.scenes;
            for (int index = 0, count = scenes.Length; index <= count; ++index)
            {
                int capturedIndex = index;
                string key = $"build_settings.scenes.{index}";
                string ReadScene()
                    => capturedIndex < EditorBuildSettings.scenes.Length
                        ? JsonUtility.ToJson(BuildSceneValue.From(EditorBuildSettings.scenes[capturedIndex]))
                        : "<append>";

                if (index == count)
                    registry.AddCollectionAppend(
                        key,
                        ReadScene,
                        value => AddBuildScene(capturedIndex, ParseBuildScene(value))
                    );
                else
                    registry.AddCollectionElement(
                        key,
                        ReadScene,
                        value => SetBuildScene(capturedIndex, ParseBuildScene(value)),
                        () => RemoveBuildScene(capturedIndex)
                    );

                if (index == count)
                    continue;

                registry.Add(
                    $"build_settings.scenes.{index}.path",
                    () => EditorBuildSettings.scenes[capturedIndex].path,
                    value => UpdateBuildScene(capturedIndex, scene => new(value, scene.enabled))
                );
                registry.Add(
                    $"build_settings.scenes.{index}.enabled",
                    () => EditorBuildSettings.scenes[capturedIndex].enabled,
                    value => UpdateBuildScene(capturedIndex, scene => new(scene.path, value))
                );
            }

            static EditorBuildSettingsScene ParseBuildScene(string value)
            {
                var parsed = JsonUtility.FromJson<BuildSceneValue>(value)
                             ?? throw new FormatException(
                                 "A build scene requires JSON with path and enabled fields."
                             );
                return new(parsed.path ?? string.Empty, parsed.enabled);
            }

            static void AddBuildScene(int index, EditorBuildSettingsScene scene)
            {
                var current = EditorBuildSettings.scenes.ToList();
                if (index != current.Count)
                    throw new InvalidOperationException(
                        $"Append at index {index} is invalid; the next build scene index is {current.Count}."
                    );
                current.Add(scene);
                EditorBuildSettings.scenes = current.ToArray();
            }

            static void SetBuildScene(int index, EditorBuildSettingsScene scene)
            {
                var current = EditorBuildSettings.scenes;
                if (index >= current.Length)
                    throw new InvalidOperationException($"Build scene index {index} does not exist.");
                current[index] = scene;
                EditorBuildSettings.scenes = current;
            }

            static void RemoveBuildScene(int index)
            {
                var current = EditorBuildSettings.scenes.ToList();
                if (index >= current.Count)
                    throw new InvalidOperationException($"Build scene index {index} does not exist.");
                current.RemoveAt(index);
                EditorBuildSettings.scenes = current.ToArray();
            }

            static void UpdateBuildScene(
                int index,
                Func<EditorBuildSettingsScene, EditorBuildSettingsScene> update)
            {
                var current = EditorBuildSettings.scenes;
                if (index >= current.Length)
                    throw new InvalidOperationException($"Build scene index {index} does not exist.");
                current[index] = update(current[index]);
                EditorBuildSettings.scenes = current;
            }
        }

        [ConduitProjectSettingsProvider]
        static void RegisterConduitSettings(ProjectSettingsRegistry registry)
        {
            var targets = Enum.GetValues(typeof(BuildTarget))
                .Cast<BuildTarget>()
                .Select(target => (Target: target, Group: BuildPipeline.GetBuildTargetGroup(target)))
                .Where(target => target.Group != BuildTargetGroup.Unknown)
                .Where(target => BuildPipeline.IsBuildTargetSupported(target.Group, target.Target))
                .Select(target => NamedBuildTarget.FromBuildTargetGroup(target.Group))
                .Where(target => !string.IsNullOrWhiteSpace(target.TargetName))
                .GroupBy(target => target.TargetName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());

            foreach (var target in targets)
            {
                registry.Add(
                    $"conduit_settings.platforms.{target.TargetName}.enable_in_development_mode",
                    () =>
                    {
                        PlayerSettings.GetScriptingDefineSymbols(target, out var defines);
                        return Array.IndexOf(defines, ConduitDevelopmentDefine) >= 0;
                    },
                    enabled =>
                    {
                        PlayerSettings.GetScriptingDefineSymbols(target, out var defines);
                        bool currentlyEnabled = Array.IndexOf(defines, ConduitDevelopmentDefine) >= 0;
                        if (currentlyEnabled == enabled)
                            return;

                        var updated = new List<string>(defines);
                        if (enabled)
                            updated.Add(ConduitDevelopmentDefine);
                        else
                            updated.RemoveAll(static define => define == ConduitDevelopmentDefine);
                        PlayerSettings.SetScriptingDefineSymbols(target, updated.ToArray());
                    }
                );
            }
        }

        [ConduitProjectSettingsProvider]
        static void RegisterBuildProfiles(ProjectSettingsRegistry registry)
        {
            if (ResolveType("UnityEditor.Build.Profile.BuildProfile") is not { } type)
                return;

            var getActive = type.GetMethod("GetActiveBuildProfile", BindingFlags.Public | BindingFlags.Static);
            var setActive = type.GetMethod(
                "SetActiveBuildProfile",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { type },
                null
            );
            if (getActive != null && setActive != null)
                registry.Add(
                    "build_settings.active_profile",
                    () => getActive.Invoke(null, null) is not Object profile
                        ? "global"
                        : AssetDatabase.GetAssetPath(profile),
                    value =>
                    {
                        Object? profile = null;
                        if (value is not ("null" or "global"))
                        {
                            string path = value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                                ? value
                                : AssetDatabase.GUIDToAssetPath(value);
                            profile = AssetDatabase.LoadAssetAtPath(path, type);
                            if (profile == null)
                                throw new FormatException($"'{value}' does not resolve to a BuildProfile asset.");
                        }

                        if (ReferenceEquals(getActive.Invoke(null, null), profile))
                            return;

                        setActive.Invoke(null, new object?[] { profile });
                    }
                );

            RegisterAssetsOfType(registry, type, "build_profiles");
        }

        [ConduitProjectSettingsProvider]
        static void RegisterRenderPipelineAssets(ProjectSettingsRegistry registry)
        {
            RegisterAssetsOfType(
                registry,
                typeof(RenderPipelineAsset),
                "render_pipeline_assets"
            );

            if (ResolveType("UnityEngine.Rendering.RenderPipelineGlobalSettings") is { } globalSettingsType)
                RegisterAssetsOfType(
                    registry,
                    globalSettingsType,
                    "render_pipeline_global_settings"
                );

        }

        [ConduitProjectSettingsProvider]
        static void RegisterInputSystemSettings(ProjectSettingsRegistry registry)
        {
            RegisterSingleton(
                registry,
                "UnityEngine.InputSystem.InputSystem",
                "settings",
                "input_system_settings",
                MapInputSettingsPath,
                requireProjectAssetForWrite: true
            );
            RegisterStaticObjectProperty(
                registry,
                "UnityEngine.InputSystem.InputSystem",
                "settings",
                "input_system_settings.settings_asset"
            );
            RegisterStaticObjectProperty(
                registry,
                "UnityEngine.InputSystem.InputSystem",
                "actions",
                "input_system_settings.project_wide_actions"
            );
            RegisterAssetsOfTypeName(
                registry,
                "UnityEngine.InputSystem.InputSettings",
                "input_system_settings.assets",
                MapInputSettingsPath
            );
        }

        [ConduitProjectSettingsProvider]
        static void RegisterTextMeshProSettings(ProjectSettingsRegistry registry)
            => RegisterSingleton(
                registry,
                "TMPro.TMP_Settings",
                "instance",
                "text_mesh_pro_settings",
                requireProjectAssetForWrite: true
            );

        [ConduitProjectSettingsProvider]
        static void RegisterShaderGraphSettings(ProjectSettingsRegistry registry)
            => RegisterSingleton(
                registry,
                "UnityEditor.ShaderGraph.ShaderGraphProjectSettings",
                "instance",
                "shader_graph_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterUrpProjectSettings(ProjectSettingsRegistry registry)
            => RegisterSingleton(
                registry,
                "UnityEditor.Rendering.Universal.UniversalProjectSettings",
                "instance",
                "urp_project_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterUiToolkitSettings(ProjectSettingsRegistry registry)
        {
            RegisterSingleton(
                registry,
                "UnityEditor.UIElements.UIToolkitProjectSettings",
                "instance",
                "ui_toolkit_settings"
            );
            RegisterAssetsOfTypeName(
                registry,
                "UnityEngine.UIElements.PanelSettings",
                "ui_toolkit_settings.panel_settings"
            );
        }

        [ConduitProjectSettingsProvider]
        static void RegisterPackageManagerSettings(ProjectSettingsRegistry registry)
            => RegisterSingleton(
                registry,
                "UnityEditor.PackageManager.UI.Internal.PackageManagerProjectSettings",
                "instance",
                "package_manager_settings",
                MapPackageManagerPath
            );

        [ConduitProjectSettingsProvider]
        static void RegisterTimelineSettings(ProjectSettingsRegistry registry)
            => RegisterSingleton(registry, "TimelineProjectSettings", "instance", "timeline_settings");

        [ConduitProjectSettingsProvider]
        static void RegisterProjectAuditorSettings(ProjectSettingsRegistry registry)
            => RegisterSingleton(
                registry,
                "Unity.ProjectAuditor.Editor.ProjectAuditorSettings",
                "instance",
                "project_auditor_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterAddressablesSettings(ProjectSettingsRegistry registry)
            => RegisterAssetsOfTypeName(
                registry,
                "UnityEditor.AddressableAssets.Settings.AddressableAssetSettings",
                "addressables_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterLocalizationSettings(ProjectSettingsRegistry registry)
            => RegisterAssetsOfTypeName(
                registry,
                "UnityEngine.Localization.Settings.LocalizationSettings",
                "localization_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterXrPluginManagementSettings(ProjectSettingsRegistry registry)
            => RegisterAssetsOfTypeName(
                registry,
                "UnityEngine.XR.Management.XRGeneralSettingsPerBuildTarget",
                "xr_plugin_management_settings"
            );

        static void RegisterStaticObjectProperty(
            ProjectSettingsRegistry registry,
            string typeName,
            string propertyName,
            string key)
        {
            if (ResolveType(typeName)?.GetProperty(
                    propertyName,
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Static
                    | BindingFlags.FlattenHierarchy
                ) is not { } property
                || !typeof(Object).IsAssignableFrom(property.PropertyType))
                return;

            registry.Add(
                key,
                () => property.GetValue(null) is Object value
                    ? ProjectSettingValueCodec.FormatObjectReference(value)
                    : "null",
                property.SetMethod == null
                    ? null
                    : value =>
                    {
                        var parsed = value == "null"
                            ? null
                            : ProjectSettingValueCodec.ParseObjectReference(value, property.PropertyType);
                        property.SetValue(null, parsed);
                        AssetDatabase.SaveAssets();
                    }
            );
        }

        static string? MapInputSettingsPath(string path)
        {
            string key = SerializedProjectSettingsProvider.ToKey(path);
            if (!key.StartsWith("i_os_settings", StringComparison.Ordinal))
                return key;

            string suffix = key["i_os_settings".Length..].TrimStart('.');
            return suffix.Length == 0 ? "ios" : "ios." + suffix;
        }

        static void RegisterSingleton(
            ProjectSettingsRegistry registry,
            string typeName,
            string propertyName,
            string prefix,
            Func<string, string?>? mapPath = null,
            bool requireProjectAssetForWrite = false)
        {
            if (ResolveType(typeName)?.GetProperty(
                    propertyName,
                    BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Static
                    | BindingFlags.FlattenHierarchy
                )?.GetValue(null) is not Object target)
                return;

            // immutable package defaults remain searchable even when the project has no override asset.
            bool writable = !requireProjectAssetForWrite
                            || AssetDatabase.GetAssetPath(target)
                                .StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);

            SerializedProjectSettingsProvider.RegisterObject(
                registry,
                prefix,
                target,
                writable ? () => SaveSingleton(target) : null,
                mapPath
            );
        }

        static string? MapPackageManagerPath(string path)
        {
            string key = SerializedProjectSettingsProvider.ToKey(path);
            return key.Contains("expanded", StringComparison.Ordinal)
                   || key.Contains("one_time", StringComparison.Ordinal)
                   || key.Contains("draft", StringComparison.Ordinal)
                   || key.Contains("user_adding", StringComparison.Ordinal)
                   || key.Contains("user_selected", StringComparison.Ordinal)
                   || key.Contains("entity_id", StringComparison.Ordinal)
                   || key.Contains("compliance", StringComparison.Ordinal)
                ? null
                : key;
        }

        [ConduitProjectSettingsProvider]
        static void RegisterBurstSettings(ProjectSettingsRegistry registry)
        {
            if (ResolveType("Unity.Burst.Editor.BurstPlatformAotSettings") is not { } type)
                return;

            var getSettings = type.GetMethod(
                "GetOrCreateSettings",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static
            );
            var save = type.GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var getPath = type.GetMethod(
                "GetPath",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static
            );
            if (getSettings == null || save == null || getPath == null)
                return;

            Register(null, "common");
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in Enum.GetValues(typeof(BuildTarget)).Cast<BuildTarget>())
            {
                var group = BuildPipeline.GetBuildTargetGroup(target);
                if (group == BuildTargetGroup.Unknown || !BuildPipeline.IsBuildTargetSupported(group, target))
                    continue;

                string? path = getPath.Invoke(null, new object?[] { target }) as string;
                if (path == null || !seenPaths.Add(path))
                    continue;
                Register(target, PlatformKey(target.ToString()));
            }

            void Register(BuildTarget? target, string key)
            {
                if (getSettings.Invoke(null, new object?[] { target }) is not Object settings)
                    return;

                SerializedProjectSettingsProvider.RegisterObject(
                    registry,
                    $"burst_aot_settings.{key}",
                    settings,
                    () => save.Invoke(settings, new object?[] { target }),
                    path => MapBurstPath(path, target)
                );
            }

            string? MapBurstPath(string path, BuildTarget? target)
            {
                if (path == "Version"
                    || target == null && path != "DisabledWarnings"
                    || target != null && path == "DisabledWarnings")
                    return null;

                var shouldSerialize = type.GetMethod(
                    path + "_Serialise",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static
                );
                if (target is { } buildTarget
                    && shouldSerialize?.Invoke(null, new object[] { buildTarget }) is false)
                    return null;

                return SerializedProjectSettingsProvider.ToKey(path);
            }
        }

        static void RegisterAssetsOfTypeName(
            ProjectSettingsRegistry registry,
            string typeName,
            string prefix,
            Func<string, string?>? mapPath = null)
        {
            if (ResolveType(typeName) is not { } type)
                return;

            RegisterAssetsOfType(
                registry,
                type,
                prefix,
                mapPath: mapPath
            );
        }

        static void RegisterAssetsOfType(
            ProjectSettingsRegistry registry,
            Type type,
            string prefix,
            Func<string, string?>? mapPath = null)
        {
            var candidates = AssetDatabase.FindAssets($"t:{type.Name}")
                .Select(guid => (Guid: guid, Path: AssetDatabase.GUIDToAssetPath(guid)))
                .Where(candidate => candidate.Path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => (
                    candidate.Guid,
                    Asset: AssetDatabase.LoadAssetAtPath(candidate.Path, type)
                ))
                .Where(candidate => candidate.Asset != null)
                .Select(candidate => (candidate.Guid, Asset: candidate.Asset!))
                .ToList();
            var duplicateNames = candidates
                .GroupBy(candidate => ProjectSettingKey.Canonicalize(candidate.Asset.name))
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                string name = ProjectSettingKey.Canonicalize(candidate.Asset.name);
                if (name.Length == 0 || duplicateNames.Contains(name))
                    name = (name.Length == 0 ? "asset" : name)
                           + "_"
                           + candidate.Guid[..8].ToLowerInvariant();
                var asset = candidate.Asset;
                SerializedProjectSettingsProvider.RegisterObject(
                    registry,
                    $"{prefix}.{name}",
                    asset,
                    AssetDatabase.SaveAssets,
                    mapPath
                );
            }
        }

        static void SaveSingleton(Object target)
        {
            const BindingFlags flags = BindingFlags.Public
                                       | BindingFlags.NonPublic
                                       | BindingFlags.Instance
                                       | BindingFlags.Static;
            var save = target.GetType().GetMethod(
                "Save",
                flags,
                null,
                Type.EmptyTypes,
                null
            );
            if (save != null)
            {
                save.Invoke(save.IsStatic ? null : target, null);
                return;
            }

            // scriptable singletons persist ProjectSettings only through their Save(bool) hook.
            save = target.GetType().GetMethod(
                "Save",
                flags,
                null,
                new[] { typeof(bool) },
                null
            );
            if (save != null)
            {
                save.Invoke(save.IsStatic ? null : target, new object[] { true });
                return;
            }

            AssetDatabase.SaveAssets();
        }

        static Type? ResolveType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType(fullName, throwOnError: false) is { } type)
                    return type;

            string shortName = fullName[(fullName.LastIndexOf('.') + 1)..];
            return TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .FirstOrDefault(type => type.FullName == fullName || type.Name == shortName);
        }

        static string PlatformKey(string value)
            => ProjectSettingKey.Canonicalize(value) switch
            {
                "i_phone" or "i_os" => "ios",
                "tv_os"              => "tvos",
                "vision_os"          => "visionos",
                "web_gl"             => "webgl",
                "wsa_player"         => "windows_store_apps",
                var key               => key,
            };

        [Serializable]
        sealed class BuildSceneValue
        {
            [SerializeField]
            internal string? path;

            [SerializeField]
            internal bool enabled = true;

            internal static BuildSceneValue From(EditorBuildSettingsScene scene)
                => new() { path = scene.path, enabled = scene.enabled };
        }
    }
}
