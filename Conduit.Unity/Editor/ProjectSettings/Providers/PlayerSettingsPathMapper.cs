#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace Conduit
{
    static class PlayerSettingsPathMapper
    {
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

        internal static string? Map(
            string propertyPath,
            IReadOnlyDictionary<string, List<string>> maps)
        {
            if (maps.ContainsKey(propertyPath))
                return null;

            if (!SerializedPropertyPathParser.TryParseMapElement(
                    propertyPath,
                    out var map,
                    out var index,
                    out var field
                )
                || !maps.TryGetValue(map, out var keys))
                return RelocatePlayerSetting(SerializedProjectSettingsProvider.ToKey(propertyPath));

            if (index >= keys.Count || !field.StartsWith("second", StringComparison.Ordinal))
                return null;

            string setting = SerializedProjectSettingsProvider.ToKey(map);
            if (setting.StartsWith("build_target_", StringComparison.Ordinal))
                setting = setting["build_target_".Length..];
            if (setting.EndsWith("_per_platform", StringComparison.Ordinal))
                setting = setting[..^"_per_platform".Length];

            string suffix = string.Empty;
            if (field.Length > "second".Length)
            {
                string nested = SerializedProjectSettingsProvider.ToKey(
                    "value." + field[("second".Length + 1)..].ToString()
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

        internal static Dictionary<string, List<string>> ReadSerializedMapKeys(string path)
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
                    keys.Add(ProjectSettingsAssetRegistration.PlatformKey(value));
                }

                if (keys.Count > 0)
                    result[iterator.propertyPath] = keys;
            }
            while (iterator.NextVisible(false));

            return result;
        }
    }
}
