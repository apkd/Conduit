#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class ProjectSettingsAssetRegistration
    {
        static readonly string[] assetsSearchFolders = { "Assets" };

        internal static void RegisterAssetsOfTypeName(
            ProjectSettingsRegistry registry,
            string typeName,
            string prefix,
            Func<string, string?>? mapPath = null)
        {
            if (ProjectSettingsTypeResolver.Resolve(typeName) is not { } type)
                return;

            RegisterAssetsOfType(
                registry,
                type,
                prefix,
                mapPath: mapPath
            );
        }

        internal static void RegisterAssetsOfType(
            ProjectSettingsRegistry registry,
            Type type,
            string prefix,
            Func<string, string?>? mapPath = null)
        {
            var guids = AssetDatabase.FindAssets($"t:{type.Name}", assetsSearchFolders);
            var candidates = new List<(string Guid, Object Asset, string Name)>(guids.Length);
            var nameCounts = new Dictionary<string, int>(guids.Length, StringComparer.Ordinal);
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                    || AssetDatabase.LoadAssetAtPath(path, type) is not { } asset)
                    continue;

                var name = ProjectSettingKey.Canonicalize(asset.name);
                candidates.Add((guid, asset, name));
                nameCounts.TryGetValue(name, out var count);
                nameCounts[name] = count + 1;
            }

            foreach (var candidate in candidates)
            {
                var name = candidate.Name;
                if (name.Length == 0 || nameCounts[name] > 1)
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

        internal static string PlatformKey(string value)
            => ProjectSettingKey.Canonicalize(value) switch
            {
                "i_phone" or "i_os" => "ios",
                "tv_os"              => "tvos",
                "vision_os"          => "visionos",
                "web_gl"             => "webgl",
                "wsa_player"         => "windows_store_apps",
                var key               => key,
            };
    }
}
