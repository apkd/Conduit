#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class ConduitToolUsage
    {
        internal const string RestartStartedUtcTicksEnvironmentVariable =
            "CONDUIT_RESTART_STARTED_UTC_TICKS";
        internal const string EnabledPreferenceKey = "Conduit.LocalToolUsage.Enabled";
        internal const string AllProjectsDataPreferenceKey = "Conduit.LocalToolUsage.AllProjects";

        // the path isolates projects that happen to share the same PlayerPrefs identity.
        internal static readonly string ProjectDataPreferenceKey =
            $"Conduit.LocalToolUsage.Project:{ConduitProjectIdentity.GetProjectPath()}";

        // sorted names keep zero-count tools visible and make validation allocation-free.
        // help is server-only, so it has no meaningful Unity-side duration.
        internal static readonly string[] ToolNames =
        {
            BridgeCommandTypes.Detour,
            BridgeCommandTypes.DiscardScenes,
            BridgeCommandTypes.EditMode,
            BridgeCommandTypes.ExecuteCode,
            BridgeCommandTypes.FindMissingScripts,
            BridgeCommandTypes.FindReferencesTo,
            BridgeCommandTypes.FromJsonOverwrite,
            BridgeCommandTypes.GetDependencies,
            BridgeCommandTypes.PlayMode,
            BridgeCommandTypes.ProfilerBrowse,
            BridgeCommandTypes.ProfilerOverview,
            BridgeCommandTypes.ProfilerRecord,
            BridgeCommandTypes.ProjectSettings,
            BridgeCommandTypes.Reflect,
            BridgeCommandTypes.RefreshAssetDatabase,
            BridgeCommandTypes.ReimportAssets,
            BridgeCommandTypes.Restart,
            BridgeCommandTypes.RunTestsEditMode,
            BridgeCommandTypes.RunTestsPlayer,
            BridgeCommandTypes.RunTestsPlayMode,
            BridgeCommandTypes.SaveScenes,
            BridgeCommandTypes.Screenshot,
            BridgeCommandTypes.Search,
            BridgeCommandTypes.Show,
            BridgeCommandTypes.Status,
            BridgeCommandTypes.ToJson,
            BridgeCommandTypes.ViewBurstAsm,
        };

        internal static event Action? DataChanged;

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPreferenceKey, true);
            set => EditorPrefs.SetBool(EnabledPreferenceKey, value);
        }

        internal static long BeginCall(string toolName)
        {
            try
            {
                // wall-clock ticks remain meaningful across domain reloads and process replacement.
                return Enabled && IsTrackedTool(toolName)
                    ? DateTime.UtcNow.Ticks
                    : 0L;
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Failed to start local tool usage tracking.", exception);
                return 0L;
            }
        }

        internal static void CompleteCall(string toolName, long startedUtcTicks)
        {
            try
            {
                if (!Enabled || startedUtcTicks <= 0L)
                    return;

                long completedUtcTicks = DateTime.UtcNow.Ticks;
                if (startedUtcTicks > completedUtcTicks)
                    return;

                RecordDuration(toolName, TimeSpan.FromTicks(completedUtcTicks - startedUtcTicks).TotalMilliseconds);
            }
            catch (Exception exception)
            {
                // tracking is observational and must never change a tool call's result
                ConduitDiagnostics.Error("Failed to finish local tool usage tracking.", exception);
            }
        }

        internal static ToolUsageRecord[] GetProjectData()
            => BuildRecords(Read(PlayerPrefs.GetString(ProjectDataPreferenceKey, string.Empty)));

        internal static ToolUsageRecord[] GetAllProjectsData()
            => BuildRecords(Read(EditorPrefs.GetString(AllProjectsDataPreferenceKey, string.Empty)));

        internal static void DeleteAllStoredData()
        {
            PlayerPrefs.DeleteKey(ProjectDataPreferenceKey);
            PlayerPrefs.Save();
            EditorPrefs.DeleteKey(AllProjectsDataPreferenceKey);
            DataChanged?.Invoke();
        }

        internal static void CompleteRestartFromEnvironment()
        {
            // import workers inherit the launch environment but must not duplicate the main editor's sample.
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            try
            {
                string? value = Environment.GetEnvironmentVariable(
                    RestartStartedUtcTicksEnvironmentVariable
                );
                if (value is null)
                    return;

                // clearing prevents later domain reloads from counting the same process launch again.
                Environment.SetEnvironmentVariable(RestartStartedUtcTicksEnvironmentVariable, null);
                if (long.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var startedUtcTicks
                    ))
                    CompleteCall(BridgeCommandTypes.Restart, startedUtcTicks);
            }
            catch (Exception exception)
            {
                // startup telemetry is observational and must not prevent the package from loading.
                ConduitDiagnostics.Error("Failed to finish restart usage tracking.", exception);
            }
        }

        internal static void RecordDuration(string toolName, double durationMilliseconds)
        {
            if (!IsTrackedTool(toolName)
                || durationMilliseconds < 0d
                || double.IsNaN(durationMilliseconds)
                || double.IsInfinity(durationMilliseconds))
                return;

            // editor preferences cannot enumerate other projects' PlayerPrefs, so both aggregates update here.
            var projectData = Read(PlayerPrefs.GetString(ProjectDataPreferenceKey, string.Empty));
            AddSample(projectData, toolName, durationMilliseconds);
            PlayerPrefs.SetString(ProjectDataPreferenceKey, JsonUtility.ToJson(projectData));
            PlayerPrefs.Save();

            var allProjectsData = Read(EditorPrefs.GetString(AllProjectsDataPreferenceKey, string.Empty));
            AddSample(allProjectsData, toolName, durationMilliseconds);
            EditorPrefs.SetString(AllProjectsDataPreferenceKey, JsonUtility.ToJson(allProjectsData));
            DataChanged?.Invoke();
        }

        static void AddSample(StoredToolUsageData data, string toolName, double durationMilliseconds)
        {
            StoredToolUsageEntry? entry = null;
            foreach (var candidate in data.tools)
            {
                if (candidate is null || candidate.tool_name != toolName)
                    continue;

                entry = candidate;
                break;
            }

            if (entry is null)
            {
                entry = new() { tool_name = toolName };
                data.tools.Add(entry);
            }

            // the online mean is exact and keeps storage constant regardless of sample count.
            entry.average_duration_ms +=
                (durationMilliseconds - entry.average_duration_ms) / (entry.call_count + 1L);
            entry.call_count++;
        }

        static StoredToolUsageData Read(string json)
        {
            if (json.Length == 0)
                return new();

            try
            {
                var data = JsonUtility.FromJson<StoredToolUsageData>(json) ?? new();
                data.tools ??= new();
                return data;
            }
            catch (ArgumentException)
            {
                // malformed local preferences are disposable; the next sample replaces them.
                return new();
            }
        }

        static ToolUsageRecord[] BuildRecords(StoredToolUsageData data)
        {
            var storedByTool = new Dictionary<string, StoredToolUsageEntry>(StringComparer.Ordinal);
            foreach (var entry in data.tools)
                if (entry is not null && IsTrackedTool(entry.tool_name))
                    storedByTool[entry.tool_name] = entry;

            var records = new ToolUsageRecord[ToolNames.Length];
            for (int index = 0, count = records.Length; index < count; ++index)
            {
                string toolName = ToolNames[index];
                records[index] = storedByTool.TryGetValue(toolName, out var entry)
                    ? new(toolName, entry.call_count, entry.average_duration_ms)
                    : new(toolName, 0L, 0d);
            }

            return records;
        }

        static bool IsTrackedTool(string toolName)
            => Array.BinarySearch(ToolNames, toolName, StringComparer.Ordinal) >= 0;

        [Serializable]
        sealed class StoredToolUsageData
        {
            public List<StoredToolUsageEntry> tools = new();
        }

        [Serializable]
        sealed class StoredToolUsageEntry
        {
            public string tool_name = string.Empty;
            public long call_count;
            public double average_duration_ms;
        }
    }

    readonly struct ToolUsageRecord
    {
        internal readonly string ToolName;
        internal readonly long CallCount;
        internal readonly double AverageDurationMilliseconds;

        internal ToolUsageRecord(string toolName, long callCount, double averageDurationMilliseconds)
        {
            ToolName = toolName;
            CallCount = callCount;
            AverageDurationMilliseconds = averageDurationMilliseconds;
        }
    }
}
