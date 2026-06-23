#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static void StartPlayToggle()
        {
            InstallPlayModeHooks();
            ResetPlayModeState();
            TryAdvancePlayToggle();
        }

        static void StartReimport(PendingOperationState operation)
        {
            if (TryCompleteReimportPlayModeBlock(operation.command_type))
                return;

            // reload externally changed open scenes before asset refresh can raise unity's modal prompt.
            if (TryCompleteOpenSceneDiskChangeBlock(operation.command_type))
                return;

            var commandKind = ParseIncomingCommand(operation.command_type).Kind;
            List<string>? assetPaths = null;
            if (commandKind == ParsedBridgeCommandKind.ReimportAssets)
            {
                if (!TryPrepareReimportAssets(operation, out var resolvedAssetPaths))
                    return;

                assetPaths = resolvedAssetPaths;
            }

            InstallReimportHooks();
            ResetReimportSettlementState();
            if (commandKind == ParsedBridgeCommandKind.ReimportAssets)
            {
                var pathsToReimport = assetPaths!;
                StoreReimportAssetPaths(operation, pathsToReimport);
                foreach (var assetPath in pathsToReimport)
                    AssetDatabase.ImportAsset(
                        assetPath,
                        ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport
                    );
            }
            else
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }

            reimportRefreshReturned = true;
            TryFinishReimport(countIdleUpdate: false);
        }

        static void StartTestRun(TestMode mode, bool playerRun, string? rawTestFilter, bool @async = false)
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

            var runGuid = GetOrCreateTestRunnerApi().Execute(
                new ExecutionSettings(filter)
                {
                    playerHeartbeatTimeout = 600,
                }
            );

            lock (stateGate)
                activeTestRunGuid = runGuid;

            // async test runs release the MCP command queue while Unity's test runner continues in the editor.
            if (@async)
                _ = CompleteCurrentAsync(CreateAsyncTestRunStartedResult(mode));
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

        static bool TryCompleteReimportPlayModeBlock(string commandType)
        {
            if (!ShouldBlockReimportForPlayMode(EditorApplication.isPlaying))
                return false;

            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.Exception,
                    diagnostic = BuildReimportPlayModeDiagnostic(commandType),
                }
            );

            return true;
        }

        static bool TryCompleteOpenSceneDiskChangeBlock(string commandType)
        {
            if (ConduitOpenSceneDiskChangeGuard.PrepareOpenScenesForAssetRefresh(commandType) is not { Length: > 0 } diagnostic)
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

        internal static bool ShouldBlockReimportForPlayMode(bool isPlaying)
            => isPlaying;

        internal static string BuildReimportPlayModeDiagnostic(string commandType = BridgeCommandTypes.RefreshAssetDatabase)
            => $"Cannot run '{commandType}' while Unity is in play mode. Use 'editmode' to return to edit mode first.";

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
            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error)
                    continue;

                AppendCompilerMessage($"{assemblyPath}: {message.message} ({message.file}:{message.line})");
            }
        }

        static void OnReimportUpdate() => TryFinishReimport(countIdleUpdate: true);

        static void InstallTestRunCompletionHooks()
        {
            if (testRunCompletionHooksInstalled)
                return;

            testRunCompletionHooksInstalled = true;
            EditorApplication.update += OnTestRunCompletionUpdate;
        }

        static void RemoveTestRunCompletionHooks()
        {
            if (!testRunCompletionHooksInstalled)
                return;

            testRunCompletionHooksInstalled = false;
            EditorApplication.update -= OnTestRunCompletionUpdate;
        }

        static void OnTestRunCompletionUpdate() => TryCompletePendingTestRun();

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

        static void TryFinishReimport(bool countIdleUpdate)
        {
            PendingOperationState? operation;
            ParsedBridgeCommand command;
            lock (stateGate)
            {
                operation = activeOperation;
                command = activeCommand;
            }

            if (operation == null || !IsAssetImportCommand(command.Kind))
                return;

            if (!reimportRefreshReturned)
                return;

            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            if (isCompiling || isUpdating)
            {
                ResetReimportIdleSettleState();
                return;
            }

            if (countIdleUpdate && reimportIdleUpdateCount < ReimportIdleSettleUpdates)
                reimportIdleUpdateCount++;

            if (ShouldWaitForReimportIdle(reimportRefreshReturned, isCompiling, isUpdating, reimportIdleUpdateCount))
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
                    return_value = command.Kind == ParsedBridgeCommandKind.ReimportAssets
                        ? FormatReimportedAssetFilenames(operation.snippet)
                        : null,
                    diagnostic = string.IsNullOrWhiteSpace(compilerMessages)
                        ? hasRestoredCompileFailure ? BuildRestoredReimportCompileErrorDiagnostic() : null
                        : compilerMessages,
                }
            );
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

            if (operation == null || !IsEditorModeCommand(command.Kind))
                return;

            var enterPlayMode = command.Kind == ParsedBridgeCommandKind.PlayMode;

            if (!enterPlayMode)
            {
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    var changedMode = exitPlayModeRequested;
                    ResetPlayModeState();
                    _ = CompleteCurrentAsync(
                        new()
                        {
                            outcome = ToolOutcome.Success,
                            return_value = BuildPlayCompletionDiagnostic(false, changedMode, false),
                        }
                    );

                    return;
                }

                exitPlayModeRequested = true;
                EditorApplication.isPlaying = false;
                return;
            }

            if (EditorApplication.isPlaying)
            {
                var changedMode = enterPlayModeRequested;
                ResetPlayModeState();
                _ = CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.Success,
                        return_value = BuildPlayCompletionDiagnostic(true, changedMode, EditorApplication.isPaused),
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

        internal static bool ShouldWaitForTestRunCompletion(
            bool isTestRunnerActive,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode)
            => ShouldWaitForTestRunCompletion(
                isTestRunnerActive,
                isCompiling,
                isUpdating,
                isPlaying,
                isPlayingOrWillChangePlaymode,
                completeDespiteStuckTestRunner: false
            );

        internal static bool ShouldWaitForTestRunCompletion(
            bool isTestRunnerActive,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode,
            bool completeDespiteStuckTestRunner)
            => isCompiling
               || isUpdating
               || isPlaying
               || isPlayingOrWillChangePlaymode
               || isTestRunnerActive && !completeDespiteStuckTestRunner;

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

        internal static string BuildPlayCompletionDiagnostic(bool targetPlayMode, bool changedMode, bool isPaused)
            => (targetPlayMode, changedMode) switch
            {
                (true, true)   => $"Entered play mode. Paused: {(isPaused ? "yes" : "no")}.",
                (true, false)  => $"Already in play mode. Paused: {(isPaused ? "yes" : "no")}.",
                (false, true)  => "Entered edit mode.",
                (false, false) => "Already in edit mode.",
            };

        static bool IsEditorModeCommand(ParsedBridgeCommandKind kind)
            => kind is ParsedBridgeCommandKind.PlayMode or ParsedBridgeCommandKind.EditMode;

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
            exitPlayModeRequested = false;
        }

        static BridgeCommandResult CreateAsyncTestRunStartedResult(TestMode mode)
            => new()
            {
                outcome = ToolOutcome.Success,
                return_value = $"{GetTestModeDisplayName(mode)} tests are running asynchronously. You can use other tools while the test run continues.",
            };

        static string GetTestModeDisplayName(TestMode mode)
            => mode == TestMode.EditMode ? "Edit mode" : "Play mode";

        internal static bool ShouldWaitForReimportIdle(bool refreshReturned, bool isCompiling, bool isUpdating, int idleUpdateCount)
            => !refreshReturned
               || isCompiling
               || isUpdating
               || idleUpdateCount < ReimportIdleSettleUpdates;

        static bool TryPrepareReimportAssets(PendingOperationState operation, out List<string> assetPaths)
        {
            assetPaths = ConduitSearchUtility.ResolveAssetPaths(operation.target ?? string.Empty);
            if (assetPaths.Count > 0)
                return true;

            _ = CompleteCurrentAsync(
                new()
                {
                    outcome = ToolOutcome.Success,
                    return_value = "No assets matched the query.",
                }
            );

            return false;
        }

        static void StoreReimportAssetPaths(PendingOperationState operation, List<string> assetPaths)
        {
            operation.snippet = string.Join("\n", assetPaths);
            lock (stateGate)
                if (ReferenceEquals(activeOperation, operation))
                    activeOperation.snippet = operation.snippet;

            PersistActiveOperation(operation, ParsedBridgeCommandKind.ReimportAssets);
        }

        internal static string FormatReimportedAssetFilenames(string? assetPathPayload)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine("Reimported assets:");
            var appendedAny = false;
            foreach (var assetPath in SplitReimportAssetPaths(assetPathPayload))
            {
                builder.Append("- ");
                builder.Append(GetAssetFileName(assetPath));
                builder.Append('\n');
                appendedAny = true;
            }

            if (appendedAny)
                return builder.TrimEnd().ToString();

            return "No assets were reimported.";
        }

        static string[] SplitReimportAssetPaths(string? assetPathPayload)
            => string.IsNullOrWhiteSpace(assetPathPayload)
                ? Array.Empty<string>()
                : assetPathPayload!.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        static string GetAssetFileName(string assetPath)
        {
            var normalizedPath = assetPath.Replace('\\', '/').TrimEnd('/');
            var separatorIndex = normalizedPath.LastIndexOf('/');
            return separatorIndex < 0 ? normalizedPath : normalizedPath[(separatorIndex + 1)..];
        }

        static void ResetReimportSettlementState()
        {
            reimportRefreshReturned = false;
            ResetReimportIdleSettleState();
        }

        static void ResetReimportIdleSettleState()
            => reimportIdleUpdateCount = 0;

        static void ResetTestRunCompletionState()
        {
            activeTestRunGuid = null;
            pendingTestRunResult = null;
        }

        static void MarkRestoredReimportAsResumed()
        {
            reimportRefreshReturned = true;
            reimportIdleUpdateCount = ReimportIdleSettleUpdates;
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

            result.diagnostic = ConduitUtility.NormalizeDiagnostic(result.diagnostic, result.exception?.message);
            var logs = DrainLogs(operation.command_type, result.outcome, result.diagnostic);
            result.logs = discardLogs ? string.Empty : logs;
            RemoveReimportHooks();
            RemovePlayModeHooks();
            RemoveTestRunCompletionHooks();
            ResetTestRunCompletionState();
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
