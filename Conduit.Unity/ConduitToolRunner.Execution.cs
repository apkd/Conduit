#nullable enable

using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static void StartPlayToggle(PendingOperationState operation)
        {
            if (!TryGetPlayTargetMode(operation.target, out _))
            {
                _ = CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = "The 'play' command is missing its target mode.",
                    }
                );

                return;
            }

            InstallPlayModeHooks();
            ResetPlayModeState();
            TryAdvancePlayToggle();
        }

        static void StartReimport()
        {
            InstallReimportHooks();
            ResetReimportSettlementState();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            reimportRefreshReturned = true;
            TryFinishReimport();
        }

        static void StartTestRun(TestMode mode, bool playerRun, string? rawTestFilter)
        {
            EnsureTestCallbacksRegistered();
            if (TryCompleteDirtySceneBlock(activeOperation?.command_type)
                || TryCompleteBusyTestStartBlock(activeOperation?.command_type)
                || TryCompleteCompileErrorTestStartBlock(activeOperation?.command_type))
                return;

            var filter = new Filter { testMode = mode };
            run_tests.ApplyFilter(filter, rawTestFilter);
            if (playerRun)
                filter.targetPlatform = EditorUserBuildSettings.activeBuildTarget;

            GetOrCreateTestRunnerApi().Execute(
                new ExecutionSettings(filter)
                {
                    playerHeartbeatTimeout = 600,
                }
            );
        }

        static bool TryCompleteDirtySceneBlock(string? commandType)
        {
            if (ConduitSceneCommandUtility.BuildDirtySceneDiagnostic(commandType ?? string.Empty) is not { Length: > 0 } diagnostic)
                return false;

            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.DirtyScene,
                    diagnostic = diagnostic,
                }
            );

            return true;
        }

        static bool TryCompleteBusyTestStartBlock(string? commandType)
        {
            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            var isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            if (!run_tests.ShouldBlockTestRun(isCompiling, isUpdating, isPlayingOrWillChangePlaymode))
                return false;

            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = run_tests.BuildBlockedTestRunDiagnostic(
                        commandType ?? string.Empty,
                        isCompiling,
                        isUpdating,
                        isPlayingOrWillChangePlaymode
                    ),
                }
            );

            return true;
        }

        static bool TryCompleteCompileErrorTestStartBlock(string? commandType)
        {
            if (!run_tests.ShouldFailTestRunForCompileErrors(EditorUtility.scriptCompilationFailed))
                return false;

            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.CompileError,
                    diagnostic = run_tests.BuildCompileErrorTestRunDiagnostic(commandType ?? string.Empty),
                }
            );

            return true;
        }

        static void InstallReimportHooks()
        {
            if (reimportHooksInstalled)
                return;

            reimportHooksInstalled = true;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            EditorApplication.update += OnReimportUpdate;
        }

        static void RemoveReimportHooks()
        {
            if (!reimportHooksInstalled)
                return;

            reimportHooksInstalled = false;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            EditorApplication.update -= OnReimportUpdate;
            ResetReimportSettlementState();
        }

        static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            reimportObservedCompilation = true;
            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error)
                    continue;

                AppendCompilerMessage($"{assemblyPath}: {message.message} ({message.file}:{message.line})");
            }
        }

        static void OnReimportUpdate() => TryFinishReimport();

        static void InstallPlayModeHooks()
        {
            if (playModeHooksInstalled)
                return;

            playModeHooksInstalled = true;
            EditorApplication.update += OnPlayModeUpdate;
        }

        static void RemovePlayModeHooks()
        {
            if (!playModeHooksInstalled)
                return;

            playModeHooksInstalled = false;
            EditorApplication.update -= OnPlayModeUpdate;
            ResetPlayModeState();
        }

        static void OnPlayModeUpdate() => TryAdvancePlayToggle();

        static void TryFinishReimport()
        {
            PendingOperationState? operation;
            ParsedBridgeCommand command;
            lock (stateGate)
            {
                operation = activeOperation;
                command = activeCommand;
            }

            if (operation == null || command.Kind != ParsedBridgeCommandKind.RefreshAssetDatabase)
                return;

            if (!reimportRefreshReturned)
                return;

            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            var isPlaying = EditorApplication.isPlaying;
            if (ShouldWaitForReimportScriptCompilation(
                    reimportSawImportedScripts,
                    reimportObservedCompilation,
                    isCompiling,
                    isUpdating,
                    isPlaying
                ))
                return;

            if (isUpdating)
                return;

            var compilerMessages = GetCompilerMessages();
            var hasRestoredCompileFailure = operation.is_restored
                                           && string.IsNullOrWhiteSpace(compilerMessages)
                                           && EditorUtility.scriptCompilationFailed;
            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = string.IsNullOrWhiteSpace(compilerMessages) && !hasRestoredCompileFailure
                        ? ToolOutcome.Success
                        : ToolOutcome.CompileError,
                    diagnostic = string.IsNullOrWhiteSpace(compilerMessages)
                        ? hasRestoredCompileFailure ? BuildRestoredReimportCompileErrorDiagnostic() : null
                        : compilerMessages,
                }
            );
        }

        internal static void NotifyReimportedAssets(string[] importedAssets)
        {
            PendingOperationState? operation;
            ParsedBridgeCommand command;
            lock (stateGate)
            {
                operation = activeOperation;
                command = activeCommand;
            }

            if (operation == null || command.Kind != ParsedBridgeCommandKind.RefreshAssetDatabase)
                return;

            if (importedAssets == null || importedAssets.Length == 0)
                return;

            if (ContainsCompileAffectingAssetImports(importedAssets))
                reimportSawImportedScripts = true;
        }

        static void TryAdvancePlayToggle()
        {
            PendingOperationState? operation;
            ParsedBridgeCommand command;
            lock (stateGate)
            {
                operation = activeOperation;
                command = activeCommand;
            }

            if (operation == null || command.Kind != ParsedBridgeCommandKind.Play)
                return;

            if (!TryGetPlayTargetMode(operation.target, out var enterPlayMode))
            {
                _ = CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.Exception,
                        diagnostic = "The active 'play' command is missing its target mode.",
                    }
                );

                return;
            }

            if (!enterPlayMode)
            {
                ResetPlayModeState();
                if (!EditorApplication.isPlaying)
                {
                    _ = CompleteCurrentAsync(
                        new()
                        {
                            outcome = ToolOutcome.Success,
                            return_value = BuildPlayCompletionDiagnostic(false, false),
                        }
                    );

                    return;
                }

                EditorApplication.isPlaying = false;
                return;
            }

            if (EditorApplication.isPlaying)
            {
                ResetPlayModeState();
                _ = CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = BuildPlayCompletionDiagnostic(true, EditorApplication.isPaused),
                    }
                );

                return;
            }

            if (enterPlayModeRequested)
            {
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    enterPlayModeRequested = false;
                else
                    return;
            }

            if (operation.is_restored && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                enterPlayModeRequested = true;
                return;
            }

            if (ShouldWaitToEnterPlayMode(
                    EditorApplication.isCompiling,
                    EditorApplication.isUpdating,
                    EditorApplication.isPlayingOrWillChangePlaymode
                ))
            {
                EnsureEnterPlayModeBusyWaitDeadline();
                if (EditorApplication.timeSinceStartup < enterPlayModeBusyWaitDeadline)
                    return;

                _ = CompleteCurrentAsync(
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

            ResetEnterPlayModeBusyWaitDeadline();
            if (ShouldFailEnterPlayForCompileErrors(EditorUtility.scriptCompilationFailed))
            {
                _ = CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.CompileError,
                        diagnostic = BuildEnterPlayCompileErrorDiagnostic(),
                    }
                );

                return;
            }

            enterPlayModeRequested = true;
            EditorApplication.isPlaying = true;
        }

        internal static bool ShouldWaitToEnterPlayMode(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => isCompiling || isUpdating || isPlayingOrWillChangePlaymode;

        internal static bool ShouldFailEnterPlayForCompileErrors(bool scriptCompilationFailed)
            => scriptCompilationFailed;

        internal static string BuildEnterPlayBusyDiagnostic(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        static string BuildPlayCompletionDiagnostic(bool enteredPlayMode, bool isPaused)
            => enteredPlayMode
                ? $"Entered play mode. Paused: {(isPaused ? "yes" : "no")}."
                : "Entered edit mode.";

        static string GetPlayTarget(bool isPlaying)
            => isPlaying ? PlayTargetEditMode : PlayTargetPlayMode;

        static bool TryGetPlayTargetMode(string? target, out bool enterPlayMode)
        {
            if (target == PlayTargetPlayMode)
            {
                enterPlayMode = true;
                return true;
            }

            if (target == PlayTargetEditMode)
            {
                enterPlayMode = false;
                return true;
            }

            enterPlayMode = false;
            return false;
        }

        static void EnsureEnterPlayModeBusyWaitDeadline()
        {
            if (enterPlayModeBusyWaitDeadline > 0)
                return;

            enterPlayModeBusyWaitDeadline = EditorApplication.timeSinceStartup + enterPlayModeBusyWaitTimeout.TotalSeconds;
        }

        static void ResetEnterPlayModeBusyWaitDeadline()
            => enterPlayModeBusyWaitDeadline = 0;

        static void ResetPlayModeState()
        {
            ResetEnterPlayModeBusyWaitDeadline();
            enterPlayModeRequested = false;
        }

        internal static bool ShouldWaitForReimportScriptCompilation(bool sawImportedScripts, bool observedCompilation, bool isCompiling, bool isUpdating, bool isPlaying)
            => sawImportedScripts
               && (isUpdating || !isPlaying && (isCompiling || !observedCompilation));

        internal static bool ContainsCompileAffectingAssetImports(string[] importedAssets)
        {
            foreach (var assetPath in importedAssets)
                if (IsCompileAffectingAssetPath(assetPath))
                    return true;

            return false;
        }

        static bool IsCompileAffectingAssetPath(string assetPath)
            => assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               || assetPath.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
               || assetPath.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase)
               || assetPath.EndsWith(".rsp", StringComparison.OrdinalIgnoreCase)
               || assetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        static void ResetReimportSettlementState()
        {
            reimportRefreshReturned = false;
            reimportSawImportedScripts = false;
            reimportObservedCompilation = false;
        }

        static void MarkRestoredReimportAsResumed()
        {
            reimportRefreshReturned = true;
            reimportSawImportedScripts = true;
            reimportObservedCompilation = true;
        }

        internal static string BuildRestoredReimportCompileErrorDiagnostic()
            => "Asset refresh completed, but the project has compilation errors.";

        static async Task CompleteCurrentAsync(BridgeCommandResult result)
        {
            PendingOperationState? operation;
            bool discardLogs;
            var nextQueuedCommandShouldStart = false;
            lock (stateGate)
            {
                operation = activeOperation;
                if (operation == null)
                    return;

                discardLogs = discardCapturedLogsOnCompletion;
                discardCapturedLogsOnCompletion = false;
                activeOperation = null;
                activeCommand = default;
                nextQueuedCommandShouldStart = queuedOperations.Count > 0;
            }

            var logs = DrainLogs(operation.command_type, result.outcome);
            result.logs = discardLogs ? string.Empty : logs;
            result.diagnostic = ConduitUtility.NormalizeDiagnostic(result.diagnostic, result.exception?.message);
            RemoveReimportHooks();
            RemovePlayModeHooks();
            ClearPersistedActiveOperation();
            if (await ConduitConnection.TrySendResultAsync(operation.client_id, operation.request_id, result, operation.command_type))
            {
                ClearPendingResult();
                if (nextQueuedCommandShouldStart)
                    PumpQueuedCommands();

                return;
            }

            /*
             * Persist completed results so reconnecting clients can observe the same
             * completion without re-running side effects after a disconnect or timeout.
             */
            PersistPendingResult(operation.request_id, operation.command_type, result);
            if (nextQueuedCommandShouldStart)
                PumpQueuedCommands();
        }
    }
}
