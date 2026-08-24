#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitToolUsage
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

    }
}
