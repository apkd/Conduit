#nullable enable

using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class PackageProjectSettingsProviders
    {
        [ConduitProjectSettingsProvider]
        static void RegisterRenderPipelineAssets(ProjectSettingsRegistry registry)
        {
            ProjectSettingsAssetRegistration.RegisterAssetsOfType(
                registry,
                typeof(RenderPipelineAsset),
                "render_pipeline_assets"
            );

            if (ProjectSettingsTypeResolver.Resolve("UnityEngine.Rendering.RenderPipelineGlobalSettings") is { } globalSettingsType)
                ProjectSettingsAssetRegistration.RegisterAssetsOfType(
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
            ProjectSettingsAssetRegistration.RegisterAssetsOfTypeName(
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
            ProjectSettingsAssetRegistration.RegisterAssetsOfTypeName(
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
            => ProjectSettingsAssetRegistration.RegisterAssetsOfTypeName(
                registry,
                "UnityEditor.AddressableAssets.Settings.AddressableAssetSettings",
                "addressables_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterLocalizationSettings(ProjectSettingsRegistry registry)
            => ProjectSettingsAssetRegistration.RegisterAssetsOfTypeName(
                registry,
                "UnityEngine.Localization.Settings.LocalizationSettings",
                "localization_settings"
            );

        [ConduitProjectSettingsProvider]
        static void RegisterXrPluginManagementSettings(ProjectSettingsRegistry registry)
            => ProjectSettingsAssetRegistration.RegisterAssetsOfTypeName(
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
            if (ProjectSettingsTypeResolver.Resolve(typeName)?.GetProperty(
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
            if (ProjectSettingsTypeResolver.Resolve(typeName)?.GetProperty(
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
    }
}
