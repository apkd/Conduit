#nullable enable

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitToolUsage
    {
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

            internal PendingSamples(int toolCount)
            {
                counts = new long[toolCount];
                totalDurations = new double[toolCount];
            }

            internal bool HasSamples { get; private set; }

            internal void Add(int toolIndex, double durationMilliseconds)
            {
                counts[toolIndex]++;
                totalDurations[toolIndex] += durationMilliseconds;
                HasSamples = true;
            }

            internal void Apply(StoredToolUsageData data)
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

            internal void Clear()
            {
                if (!HasSamples)
                    return;

                Array.Clear(counts, 0, counts.Length);
                Array.Clear(totalDurations, 0, totalDurations.Length);
                HasSamples = false;
            }
        }

    }
}
