#nullable enable

using System;

namespace Conduit
{
    static class SerializedAssetProjectSettingsProvider
    {
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
            var playerSettingsMaps = PlayerSettingsPathMapper.ReadSerializedMapKeys("ProjectSettings/ProjectSettings.asset");
            foreach (var asset in projectSettingsAssets)
                SerializedProjectSettingsProvider.RegisterFile(
                    registry,
                    asset.Prefix,
                    asset.Path,
                    asset.Prefix switch
                    {
                        "player_settings"    => path => PlayerSettingsPathMapper.Map(path, playerSettingsMaps),
                        "tag_manager"       => MapTagManagerPath,
                        "graphics_settings" => MapGraphicsSettingsPath,
                        _                   => null,
                    }
                );
        }

        static string? MapGraphicsSettingsPath(string propertyPath)
            => propertyPath switch
            {
                "m_LogWhenShaderIsCompiled" => "log_shader_compilation",
                "m_CustomRenderPipeline"    => "default_render_pipeline",
                _                           => SerializedProjectSettingsProvider.ToKey(propertyPath),
            };

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
    }
}
