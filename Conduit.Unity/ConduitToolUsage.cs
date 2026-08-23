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
        const double FlushDelaySeconds = 1d;
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
            BridgeCommandTypes.Record,
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

        static readonly PendingSamples pendingProjectSamples = new(ToolNames.Length);
        static readonly PendingSamples pendingAllProjectsSamples = new(ToolNames.Length);
        internal static event Action? DataChanged;
        // raw-value keys avoid repeat JSON parsing while still observing writes from other editor processes.
        static string? cachedProjectDataJson;
        static string? cachedAllProjectsDataJson;
        static StoredToolUsageData? cachedProjectData;
        static StoredToolUsageData? cachedAllProjectsData;
        static ToolUsageRecord[]? cachedProjectRecords;
        static ToolUsageRecord[]? cachedAllProjectsRecords;
        static double flushAt;
        static bool flushScheduled;

        static ConduitToolUsage()
        {
            AssemblyReloadEvents.beforeAssemblyReload += FlushPending;
            EditorApplication.quitting += FlushPending;
        }

        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPreferenceKey, true);
            set
            {
                if (!value)
                    FlushPending();

                EditorPrefs.SetBool(EnabledPreferenceKey, value);
            }
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
                if (startedUtcTicks <= 0L || !Enabled)
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
        {
            EnsureProjectData();
            return cachedProjectRecords ??= BuildRecords(cachedProjectData!);
        }

        internal static ToolUsageRecord[] GetAllProjectsData()
        {
            EnsureAllProjectsData();
            return cachedAllProjectsRecords ??= BuildRecords(cachedAllProjectsData!);
        }

        internal static void DeleteAllStoredData()
        {
            CancelScheduledFlush();
            pendingProjectSamples.Clear();
            pendingAllProjectsSamples.Clear();
            PlayerPrefs.DeleteKey(ProjectDataPreferenceKey);
            PlayerPrefs.Save();
            EditorPrefs.DeleteKey(AllProjectsDataPreferenceKey);
            cachedProjectDataJson = null;
            cachedAllProjectsDataJson = null;
            cachedProjectData = null;
            cachedAllProjectsData = null;
            cachedProjectRecords = null;
            cachedAllProjectsRecords = null;
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
            int toolIndex = Array.BinarySearch(ToolNames, toolName, StringComparer.Ordinal);
            if (toolIndex < 0
                || durationMilliseconds < 0d
                || double.IsNaN(durationMilliseconds)
                || double.IsInfinity(durationMilliseconds))
                return;

            if (cachedProjectData != null)
                AddSamples(cachedProjectData, toolName, 1L, durationMilliseconds);
            if (cachedAllProjectsData != null)
                AddSamples(cachedAllProjectsData, toolName, 1L, durationMilliseconds);
            pendingProjectSamples.Add(toolIndex, durationMilliseconds);
            pendingAllProjectsSamples.Add(toolIndex, durationMilliseconds);
            cachedProjectRecords = null;
            cachedAllProjectsRecords = null;
            ScheduleFlush();
            DataChanged?.Invoke();
        }

        internal static void FlushPending()
        {
            CancelScheduledFlush();
            var flushed = false;
            if (pendingProjectSamples.HasSamples)
                flushed |= TryFlushProjectData();
            if (pendingAllProjectsSamples.HasSamples)
                flushed |= TryFlushAllProjectsData();

            if (pendingProjectSamples.HasSamples || pendingAllProjectsSamples.HasSamples)
                ScheduleFlush();
            if (flushed)
                DataChanged?.Invoke();
        }

        static void EnsureProjectData()
        {
            var json = PlayerPrefs.GetString(ProjectDataPreferenceKey, string.Empty);
            if (json == cachedProjectDataJson && cachedProjectData != null)
                return;

            cachedProjectDataJson = json;
            cachedProjectData = Read(json);
            pendingProjectSamples.Apply(cachedProjectData);
            cachedProjectRecords = null;
        }

        static void EnsureAllProjectsData()
        {
            var json = EditorPrefs.GetString(AllProjectsDataPreferenceKey, string.Empty);
            if (json == cachedAllProjectsDataJson && cachedAllProjectsData != null)
                return;

            cachedAllProjectsDataJson = json;
            cachedAllProjectsData = Read(json);
            pendingAllProjectsSamples.Apply(cachedAllProjectsData);
            cachedAllProjectsRecords = null;
        }

        static bool TryFlushProjectData()
        {
            try
            {
                var data = Read(PlayerPrefs.GetString(ProjectDataPreferenceKey, string.Empty));
                pendingProjectSamples.Apply(data);
                var json = JsonUtility.ToJson(data);
                PlayerPrefs.SetString(ProjectDataPreferenceKey, json);
                // Unity persists PlayerPrefs at shutdown; a forced flush here would add disk I/O.
                cachedProjectDataJson = json;
                cachedProjectData = data;
                cachedProjectRecords = null;
                pendingProjectSamples.Clear();
                return true;
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Failed to persist local project tool usage.", exception);
                return false;
            }
        }

        static bool TryFlushAllProjectsData()
        {
            try
            {
                var data = Read(EditorPrefs.GetString(AllProjectsDataPreferenceKey, string.Empty));
                pendingAllProjectsSamples.Apply(data);
                var json = JsonUtility.ToJson(data);
                EditorPrefs.SetString(AllProjectsDataPreferenceKey, json);
                cachedAllProjectsDataJson = json;
                cachedAllProjectsData = data;
                cachedAllProjectsRecords = null;
                pendingAllProjectsSamples.Clear();
                return true;
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Failed to persist all-project tool usage.", exception);
                return false;
            }
        }

        static void ScheduleFlush()
        {
            if (flushScheduled)
                return;

            flushScheduled = true;
            flushAt = EditorApplication.timeSinceStartup + FlushDelaySeconds;
            EditorApplication.update += FlushWhenDue;
        }

        static void FlushWhenDue()
        {
            if (EditorApplication.timeSinceStartup >= flushAt)
                FlushPending();
        }

        static void CancelScheduledFlush()
        {
            if (!flushScheduled)
                return;

            flushScheduled = false;
            EditorApplication.update -= FlushWhenDue;
        }

        static void AddSamples(
            StoredToolUsageData data,
            string toolName,
            long sampleCount,
            double totalDurationMilliseconds)
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

            var combinedCount = entry.call_count + sampleCount;
            var batchAverage = totalDurationMilliseconds / sampleCount;
            entry.average_duration_ms +=
                (batchAverage - entry.average_duration_ms) * sampleCount / combinedCount;
            entry.call_count = combinedCount;
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

        sealed class PendingSamples
        {
            readonly long[] counts;
            readonly double[] totalDurations;

            public PendingSamples(int toolCount)
            {
                counts = new long[toolCount];
                totalDurations = new double[toolCount];
            }

            public bool HasSamples { get; private set; }

            public void Add(int toolIndex, double durationMilliseconds)
            {
                counts[toolIndex]++;
                totalDurations[toolIndex] += durationMilliseconds;
                HasSamples = true;
            }

            public void Apply(StoredToolUsageData data)
            {
                if (!HasSamples)
                    return;

                for (int index = 0, count = counts.Length; index < count; ++index)
                    if (counts[index] > 0L)
                        AddSamples(
                            data,
                            ToolNames[index],
                            counts[index],
                            totalDurations[index]
                        );
            }

            public void Clear()
            {
                if (!HasSamples)
                    return;

                Array.Clear(counts, 0, counts.Length);
                Array.Clear(totalDurations, 0, totalDurations.Length);
                HasSamples = false;
            }
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
