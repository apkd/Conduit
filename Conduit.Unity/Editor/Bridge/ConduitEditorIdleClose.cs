#nullable enable

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class ConduitEditorIdleClose
    {
        const string UserProtectedKey = "Conduit.IdleClose.UserProtected";
        static readonly EditorIdleCloseState state = new(
            SessionState.GetBool(UserProtectedKey, true) ? EditorIdleOwnership.User : EditorIdleOwnership.Agent,
            EditorApplication.timeSinceStartup
        );
        static readonly string projectPath = Path.GetDirectoryName(Application.dataPath)!;
        static IDisposable? inputSubscription;
        static bool initialized;
        static bool closing;
        static bool inputWarningShown;
        static double nextCheck;

        internal static void Initialize(EditorIdleOwnership launchOwnership)
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            try
            {
                BridgeIdleCloseMarker.Clear(projectPath);
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Warn($"Could not clear the previous idle-close marker: {exception.Message}");
            }

            if (Application.isBatchMode || initialized)
                return;

            initialized = true;
            try
            {
                inputSubscription = ConduitEditorInput.Subscribe(RecordUserInteraction);
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Warn($"Could not observe Editor input: {exception.Message}");
            }

            EditorApplication.focusChanged += OnFocusChanged;
            EditorApplication.quitting += Dispose;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            if (launchOwnership == EditorIdleOwnership.Agent)
                RecordAgentInteraction();
        }

        internal static void RecordCommand(BridgeMessage message)
        {
            if (!initialized || message.command is not { } command)
                return;

            // compilation setup is agent work; automatic status probes are not user requests
            if (command.command_type != BridgeCommandTypes.Status || command.track_usage)
                RecordAgentInteraction();
        }

        static void RecordAgentInteraction()
        {
            if (state.Ownership == EditorIdleOwnership.User)
                SessionState.SetBool(UserProtectedKey, false);
            state.RecordAgentInteraction(EditorApplication.timeSinceStartup);
        }

        static void RecordUserInteraction()
        {
            if (state.Ownership == EditorIdleOwnership.User)
                return;

            state.RecordUserInteraction();
            SessionState.SetBool(UserProtectedKey, true);
        }

        internal static void Update()
        {
            double now = EditorApplication.timeSinceStartup;
            if (!initialized || closing || now < nextCheck)
                return;

            nextCheck = now + 1d;
            if (ShouldClose(now))
                TryClose();
        }

        static bool ShouldClose(double now)
        {
            var settings = ConduitSettings.instance;
            // disabled or user-owned sessions need no busy-state probes
            if (!settings.AutomaticallyCloseWhenIdle || state.Ownership == EditorIdleOwnership.User)
            {
                state.Postpone(now);
                return false;
            }

            if (inputSubscription == null && !inputWarningShown)
            {
                inputWarningShown = true;
                ConduitDiagnostics.Warn("Automatic idle closing is suspended because this Unity version's Editor input hook is unavailable.");
            }

            return state.ShouldClose(
                now,
                settings.IdleCloseMinutes,
                inputSubscription == null || IsBlocked() ? EditorIdleAvailability.Busy : EditorIdleAvailability.Idle
            );
        }

        static bool IsBlocked()
            => EditorApplication.isFocused
               || EditorApplication.isPlayingOrWillChangePlaymode
               || EditorApplication.isCompiling
               || EditorApplication.isUpdating
               || BuildPipeline.isBuildingPlayer
               || ConduitConnection.HasIncomingMessages
               || ConduitToolRunner.HasOutstandingWork
               || ConduitToolRunner.IsTestRunnerActive();

        static void TryClose()
        {
            closing = true; // save callbacks must not start another close attempt
            try
            {
                if (!ConduitEditorSaveUtility.TrySave())
                    return;

                // saving can run callbacks or accept another command before shutdown commits
                if (!ShouldClose(EditorApplication.timeSinceStartup))
                    return;

                BridgeIdleCloseMarker.Write(projectPath);
                ConduitDiagnostics.Info("Closing Unity after a long idle period. Changes have been saved.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                try
                {
                    BridgeIdleCloseMarker.Clear(projectPath);
                }
                catch (Exception cleanupException)
                {
                    ConduitDiagnostics.Warn($"Could not remove the idle-close marker: {cleanupException.Message}");
                }
                ConduitDiagnostics.Error("Could not close Unity after an idle period. The Editor will remain open.", exception);
            }
            finally
            {
                state.Postpone(EditorApplication.timeSinceStartup);
                closing = false;
            }
        }

        static void OnFocusChanged(bool focused) => state.Postpone(EditorApplication.timeSinceStartup);

        static void Dispose()
        {
            initialized = false;
            inputSubscription?.Dispose();
            inputSubscription = null;
            EditorApplication.focusChanged -= OnFocusChanged;
            EditorApplication.quitting -= Dispose;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
        }
    }
}
