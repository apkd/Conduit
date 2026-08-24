#nullable enable

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Conduit
{
    static class StatusTool
    {
        internal static string Status() => JsonUtility.ToJson(CreateSnapshot());

        static PingSnapshot CreateSnapshot()
        {
            var scenes = BuildScenes(out var dirtyScenes);
            var activeDetours = DetourRuntime.ActiveMethodNames;
            return new()
            {
                unity_version = Application.unityVersion,
                platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                editor_process_id = BridgeStatusUtility.ProcessId,
                uptime = BridgeStatusUtility.FormatDuration(
                    TimeSpan.FromSeconds(EditorApplication.timeSinceStartup)
                ),
                editor_log_path = Application.consoleLogPath,
                editor_mode = EditorApplication.isPlaying ? "play mode" : "edit mode",
                is_paused = EditorApplication.isPaused,
                is_compiling = EditorApplication.isCompiling,
                is_updating = EditorApplication.isUpdating,
                is_test_runner_active = ConduitToolRunner.IsTestRunnerActive(),
                active_test_mode = ConduitToolRunner.GetActiveTestRunMode(),
                active_command_type = ConduitToolRunner.GetActiveCommandType(),
                active_detour_count = activeDetours.Length,
                active_detours = activeDetours,
                profiler_status_line = ProfilerTool.BuildStatusLine(),
                recording_status_line = RecordTool.BuildStatusLine(),
                scenes = scenes,
                dirty_scenes = dirtyScenes,
            };
        }

        static string[] BuildScenes(out string[] dirtyScenes)
        {
            var sceneCount = SceneManager.sceneCount;
            if (sceneCount == 0)
            {
                dirtyScenes = Array.Empty<string>();
                return Array.Empty<string>();
            }

            using var pooledScenes = ConduitPool.GetPooledList<string>(out var scenes);
            using var pooledDirtyScenes = ConduitPool.GetPooledList<string>(out var dirty);
            var activeScene = SceneManager.GetActiveScene();
            for (var sceneIndex = 0; sceneIndex < sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                var scenePath = ConduitHierarchyPathUtility.FormatScenePath(scene, "untitled");
                var isDirty = scene.isDirty;
                if (isDirty)
                    dirty.Add(scenePath);

                var state = isDirty ? "dirty" : "clean";
                if (scene == activeScene)
                    state += ", active";

                scenes.Add($"{scenePath} [{state}]");
            }

            dirtyScenes = dirty.ToArray();
            return scenes.ToArray();
        }

        [Serializable]
        sealed class PingSnapshot
        {
            public string unity_version = string.Empty;
            public string platform = string.Empty;
            public int editor_process_id;
            public string uptime = string.Empty;
            public string editor_log_path = string.Empty;
            public string editor_mode = string.Empty;
            public bool is_paused;
            public bool is_compiling;
            public bool is_updating;
            public bool is_test_runner_active;
            public string? active_test_mode;
            public string? active_command_type;
            public int active_detour_count;
            public string[] active_detours = Array.Empty<string>();
            public string? profiler_status_line;
            public string? recording_status_line;
            public string[] scenes = Array.Empty<string>();
            public string[] dirty_scenes = Array.Empty<string>();
        }
    }
}
