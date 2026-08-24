#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;

namespace Conduit
{
    static class QualityProjectSettingsProvider
    {
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
            if (SerializedPropertyPathParser.TryParseArrayElement(
                    propertyPath,
                    "m_PerPlatformDefaultQuality",
                    out var platformIndex,
                    out var platformField
                ))
            {
                if (platformIndex >= catalog.Platforms.Count
                    || !platformField.Equals("second", StringComparison.Ordinal))
                    return null;
                return $"platforms.{catalog.Platforms[platformIndex]}.default_level_index";
            }

            if (!SerializedPropertyPathParser.TryParseArrayElement(
                    propertyPath,
                    "m_QualitySettings",
                    out var index,
                    out var field
                ))
                return SerializedProjectSettingsProvider.ToKey(propertyPath);

            if (index >= catalog.Levels.Count)
                return null;

            if (field.Length == 0)
                return $"quality_levels.{catalog.Levels[index]}";
            string mappedField = SerializedProjectSettingsProvider.ToKey(field.ToString()) switch
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
                    platforms.Add(ProjectSettingsAssetRegistration.PlatformKey(
                        string.IsNullOrWhiteSpace(name)
                            ? index.ToString(CultureInfo.InvariantCulture)
                            : name!
                    ));
                }

            return (names, platforms);
        }
    }
}
