#nullable enable

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Process = System.Diagnostics.Process;

namespace Conduit
{
    static class status
    {
        static readonly int editorProcessId = Process.GetCurrentProcess().Id;
        static readonly DateTimeOffset editorStartedAtUtc = GetEditorStartedAtUtc();

        public static string Status() => JsonUtility.ToJson(CreateSnapshot());

        static PingSnapshot CreateSnapshot()
            => new()
            {
                unity_version = Application.unityVersion,
                platform = EditorUserBuildSettings.activeBuildTarget.ToString(),
                editor_process_id = editorProcessId,
                uptime = FormatDuration(DateTimeOffset.UtcNow - editorStartedAtUtc),
                editor_mode = EditorApplication.isPlaying ? "play mode" : "edit mode",
                is_paused = EditorApplication.isPaused,
                is_compiling = EditorApplication.isCompiling,
                is_updating = EditorApplication.isUpdating,
                active_command_type = ConduitToolRunner.GetActiveCommandType(),
                scenes = BuildScenes(),
                dirty_scenes = ConduitSceneCommandUtility.GetDirtySceneDescriptions(),
            };

        static string[] BuildScenes()
        {
            if (SceneManager.sceneCount == 0)
                return Array.Empty<string>();

            using var pooledScenes = ConduitUtility.GetPooledList<string>(out var scenes);
            var activeScene = SceneManager.GetActiveScene();
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                var scenePath = ConduitUtility.FormatScenePath(scene, "untitled");

                var state = scene.isDirty ? "dirty" : "clean";
                if (scene == activeScene)
                    state += ", active";

                scenes.Add($"{scenePath} [{state}]");
            }

            return scenes.ToArray();
        }

        /*
         * Process.StartTime is not free and status is one of the hottest commands.
         * Cache the editor start once, then derive uptime from the cached instant.
         */
        static DateTimeOffset GetEditorStartedAtUtc()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return new(process.StartTime);
            }
            catch
            {
                return DateTimeOffset.UtcNow;
            }
        }

        static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            string? primary = null;
            string? secondary = null;

            AddPart(duration.Days, "day");
            AddPart(duration.Hours, "hour");
            AddPart(duration.Minutes, "minute");
            if (primary == null)
                AddPart(Math.Max(1, duration.Seconds), "second");

            return secondary == null ? primary ?? "0 seconds" : primary + " " + secondary;

            void AddPart(int value, string unit)
            {
                if (value <= 0 || secondary != null)
                    return;

                var part = value == 1 ? $"1 {unit}" : $"{value} {unit}s";
                if (primary == null)
                    primary = part;
                else
                    secondary = part;
            }
        }

        [Serializable]
        sealed class PingSnapshot
        {
            public string unity_version = string.Empty;
            public string platform = string.Empty;
            public int editor_process_id;
            public string uptime = string.Empty;
            public string editor_mode = string.Empty;
            public bool is_paused;
            public bool is_compiling;
            public bool is_updating;
            public string? active_command_type;
            public string[] scenes = Array.Empty<string>();
            public string[] dirty_scenes = Array.Empty<string>();
        }
    }
}
