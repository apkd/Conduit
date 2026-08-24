#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{
    static class BuildProjectSettingsProvider
    {
        const string ConduitDevelopmentDefine = "CONDUIT_INCLUDE_IN_DEBUG_BUILDS";
        static readonly Lazy<BuildTarget[]> supportedBuildTargets = new(() =>
            Enum.GetValues(typeof(BuildTarget))
                .Cast<BuildTarget>()
                .Select(target => (Target: target, Group: BuildPipeline.GetBuildTargetGroup(target)))
                .Where(target => target.Group != BuildTargetGroup.Unknown
                                 && BuildPipeline.IsBuildTargetSupported(target.Group, target.Target))
                .Select(target => target.Target)
                .ToArray()
        );
        static readonly Lazy<NamedBuildTarget[]> supportedNamedBuildTargets = new(() =>
            supportedBuildTargets.Value
                .Select(BuildPipeline.GetBuildTargetGroup)
                .Select(NamedBuildTarget.FromBuildTargetGroup)
                .Where(target => !string.IsNullOrWhiteSpace(target.TargetName))
                .GroupBy(target => target.TargetName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray()
        );

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
            foreach (var target in supportedNamedBuildTargets.Value)
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
            if (ProjectSettingsTypeResolver.Resolve("UnityEditor.Build.Profile.BuildProfile") is not { } type)
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

            ProjectSettingsAssetRegistration.RegisterAssetsOfType(registry, type, "build_profiles");
        }

        [ConduitProjectSettingsProvider]
        static void RegisterBurstSettings(ProjectSettingsRegistry registry)
        {
            if (ProjectSettingsTypeResolver.Resolve("Unity.Burst.Editor.BurstPlatformAotSettings") is not { } type)
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
            foreach (var target in supportedBuildTargets.Value)
            {
                string? path = getPath.Invoke(null, new object?[] { target }) as string;
                if (path == null || !seenPaths.Add(path))
                    continue;
                Register(target, ProjectSettingsAssetRegistration.PlatformKey(target.ToString()));
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
