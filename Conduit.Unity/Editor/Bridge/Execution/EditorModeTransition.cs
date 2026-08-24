#nullable enable

using System;
using System.Threading.Tasks;
using UnityEditor;

namespace Conduit
{
    sealed class EditorModeTransition
    {
        // play mode flags can lag for a frame or two; a short grace period avoids false busy failures
        static readonly TimeSpan enterPlayModeBusyWaitTimeout = TimeSpan.FromSeconds(1);
        readonly Func<BridgeCommandResult, Task> complete;
        BridgeCommandKind commandKind;
        bool hooksInstalled;
        bool restoredOperation;
        bool enterPlayModeRequested;
        bool exitPlayModeRequested;
        double enterPlayModeBusyWaitDeadline;

        internal EditorModeTransition(Func<BridgeCommandResult, Task> complete)
            => this.complete = complete;

        internal void Start(BridgeCommandKind commandKind, bool restoredOperation)
        {
            this.commandKind = commandKind;
            this.restoredOperation = restoredOperation;
            InstallHooks();
            ResetState();
            TryAdvance();
        }

        internal void Stop()
        {
            if (hooksInstalled)
            {
                hooksInstalled = false;
                EditorApplication.update -= OnUpdate;
            }

            commandKind = BridgeCommandKind.Unknown;
            restoredOperation = false;
            ResetState();
        }

        internal static bool ShouldWaitToEnterPlayMode(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => isCompiling || isUpdating || isPlayingOrWillChangePlaymode;

        internal static bool ShouldFailEnterPlayForCompileErrors(bool scriptCompilationFailed)
            => scriptCompilationFailed;

        internal static string BuildEnterPlayBusyDiagnostic(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append("Cannot enter play mode while Unity is busy:");

            var appendedReason = false;
            AppendReason("compiling scripts", isCompiling);
            AppendReason("updating assets", isUpdating);
            AppendReason("changing play mode", isPlayingOrWillChangePlaymode);
            builder.Append('.');
            return builder.ToString();

            void AppendReason(string reason, bool shouldAppend)
            {
                if (!shouldAppend)
                    return;

                builder.Append(appendedReason ? ", " : " ");
                builder.Append(reason);
                appendedReason = true;
            }
        }

        internal static string BuildEnterPlayCompileErrorDiagnostic()
            => "Cannot enter play mode because the project has compilation errors.";

        internal static string BuildCompletionDiagnostic(bool targetPlayMode, bool changedMode, bool isPaused)
            => ((targetPlayMode, changedMode) switch
            {
                (true, true)   => $"Entered play mode. Paused: {(isPaused ? "yes" : "no")}.",
                (true, false)  => $"Already in play mode. Paused: {(isPaused ? "yes" : "no")}.",
                (false, true)  => "Entered edit mode.",
                (false, false) => "Already in edit mode.",
            }) + DetourEditorLifecycle.BuildCompletionSuffix(targetPlayMode);

        void InstallHooks()
        {
            if (hooksInstalled)
                return;

            hooksInstalled = true;
            EditorApplication.update += OnUpdate;
        }

        void OnUpdate() => TryAdvance();

        void TryAdvance()
        {
            if (!BridgeCommandKinds.IsEditorMode(commandKind))
                return;

            var enterPlayMode = commandKind == BridgeCommandKind.PlayMode;
            if (!enterPlayMode)
            {
                TryAdvanceExitPlayMode();
                return;
            }

            TryAdvanceEnterPlayMode();
        }

        void TryAdvanceExitPlayMode()
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                var changedMode = exitPlayModeRequested;
                ResetState();
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = BuildCompletionDiagnostic(false, changedMode, false),
                    }
                );
                return;
            }

            exitPlayModeRequested = true;
            EditorApplication.isPlaying = false;
        }

        void TryAdvanceEnterPlayMode()
        {
            if (EditorApplication.isPlaying)
            {
                var changedMode = enterPlayModeRequested;
                ResetState();
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = BuildCompletionDiagnostic(true, changedMode, EditorApplication.isPaused),
                    }
                );
                return;
            }

            if (enterPlayModeRequested)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    enterPlayModeRequested = false; // unity can reject the request after scripts finish compiling
                else
                    return;
            }

            if (restoredOperation && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                // after reload, Unity may already be carrying out the persisted request
                enterPlayModeRequested = true;
                return;
            }

            if (ShouldWaitToEnterPlayMode(
                    EditorApplication.isCompiling,
                    EditorApplication.isUpdating,
                    EditorApplication.isPlayingOrWillChangePlaymode
                ))
            {
                EnsureBusyWaitDeadline();
                if (EditorApplication.timeSinceStartup < enterPlayModeBusyWaitDeadline)
                    return;

                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = BuildEnterPlayBusyDiagnostic(
                            EditorApplication.isCompiling,
                            EditorApplication.isUpdating,
                            EditorApplication.isPlayingOrWillChangePlaymode
                        ),
                    }
                );
                return;
            }

            ResetBusyWaitDeadline();
            if (ShouldFailEnterPlayForCompileErrors(EditorUtility.scriptCompilationFailed))
            {
                _ = complete(
                    new()
                    {
                        outcome = ToolOutcome.CompileError,
                        diagnostic = BuildEnterPlayCompileErrorDiagnostic(),
                    }
                );
                return;
            }

            ConduitGameView.PrepareForPlayMode();
            enterPlayModeRequested = true;
            EditorApplication.isPlaying = true;
        }

        void EnsureBusyWaitDeadline()
        {
            if (enterPlayModeBusyWaitDeadline > 0)
                return;

            enterPlayModeBusyWaitDeadline = EditorApplication.timeSinceStartup + enterPlayModeBusyWaitTimeout.TotalSeconds;
        }

        void ResetBusyWaitDeadline()
            => enterPlayModeBusyWaitDeadline = 0;

        void ResetState()
        {
            ResetBusyWaitDeadline();
            enterPlayModeRequested = false;
            exitPlayModeRequested = false;
        }
    }
}
