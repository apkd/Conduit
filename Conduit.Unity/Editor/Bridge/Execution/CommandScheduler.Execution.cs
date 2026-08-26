#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Conduit
{
    sealed partial class CommandScheduler
    {
        async Task ExecuteAcceptedCommandAsync(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            try
            {
                logCapture.Start(commandKind);
                assetImportMonitor.ClearCompilerMessages();

                switch (commandKind)
                {
                    case BridgeCommandKind.PlayMode:
                    case BridgeCommandKind.EditMode:
                        editorModeTransition.Start(commandKind, operation.IsRestored);
                        break;
                    case BridgeCommandKind.Screenshot:
                        await ExecuteScreenshotAsync(operation);
                        break;
                    case BridgeCommandKind.Record:
                        await ExecuteRecordAsync(operation);
                        break;
                    case BridgeCommandKind.GetDependencies:
                        await ExecuteGetDependenciesAsync(operation);
                        break;
                    case BridgeCommandKind.FindReferencesTo:
                        await ExecuteFindReferencesToAsync(operation);
                        break;
                    case BridgeCommandKind.FindMissingScripts:
                        await ExecuteFindMissingScriptsAsync(operation);
                        break;
                    case BridgeCommandKind.Show:
                        await ExecuteShowAsync(operation);
                        break;
                    case BridgeCommandKind.Search:
                        await ExecuteSearchAsync(operation);
                        break;
                    case BridgeCommandKind.ToJson:
                        await ExecuteToJsonAsync(operation);
                        break;
                    case BridgeCommandKind.FromJsonOverwrite:
                        await ExecuteFromJsonOverwriteAsync(operation);
                        break;
                    case BridgeCommandKind.SaveScenes:
                        await ExecuteSaveScenesAsync(operation);
                        break;
                    case BridgeCommandKind.DiscardScenes:
                        await ExecuteDiscardScenesAsync(operation);
                        break;
                    case BridgeCommandKind.RefreshAssetDatabase:
                    case BridgeCommandKind.ReimportAssets:
                        assetImportMonitor.Start(operation, commandKind);
                        break;
                    case BridgeCommandKind.ExecuteCode:
                        await ExecuteCodeAsync(operation);
                        break;
                    case BridgeCommandKind.Detour:
                        await ExecuteDetourAsync(operation);
                        break;
                    case BridgeCommandKind.CompilationReferences:
                        await CompleteCurrentAsync(CompilationReferencesTool.GetManifest());
                        break;
                    case BridgeCommandKind.AssemblyBlob:
                        await CompleteCurrentAsync(CompilationReferencesTool.GetAssemblyBlobs(operation.Args));
                        break;
                    case BridgeCommandKind.ViewBurstAsm:
                        await ExecuteViewBurstAsmAsync(operation);
                        break;
                    case BridgeCommandKind.Reflect:
                        await ExecuteReflectAsync(operation);
                        break;
                    case BridgeCommandKind.ProjectSettings:
                        await ExecuteProjectSettingsAsync(operation);
                        break;
                    case BridgeCommandKind.RunTestsEditMode:
                        testRunMonitor.Start(operation, commandKind, TestMode.EditMode, playerRun: false);
                        break;
                    case BridgeCommandKind.RunTestsPlayMode:
                        testRunMonitor.Start(operation, commandKind, TestMode.PlayMode, playerRun: false);
                        break;
                    case BridgeCommandKind.RunTestsPlayer:
                        testRunMonitor.Start(operation, commandKind, TestMode.PlayMode, playerRun: true);
                        break;
                    case BridgeCommandKind.ProfilerRecord:
                        await ExecuteProfilerRecordAsync(operation);
                        break;
                    case BridgeCommandKind.ProfilerOverview:
                        await ExecuteProfilerOverviewAsync(operation);
                        break;
                    case BridgeCommandKind.ProfilerBrowse:
                        await ExecuteProfilerBrowseAsync(operation);
                        break;
                    case BridgeCommandKind.ProfilerHasMarker:
                        await ExecuteProfilerHasMarkerAsync(operation);
                        break;
                    default:
                        await CompleteCurrentAsync(
                            BridgeCommandResult.UnsupportedEditorTool(operation.CommandType)
                        );

                        break;
                }
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error($"Unhandled exception while executing '{operation.CommandType}'.", exception);
                await CompleteUnhandledCommandExceptionAsync(operation, exception);
            }
        }

        void ResumeRestoredOperation()
        {
            if (activeOperation is not { IsRestored: true } operation)
                return;

            if (!BridgeCommandKinds.IsTest(operation.Kind))
                OperationPersistence.ClearPendingTestCompletion();

            // recovery observes editor-owned work already in progress instead of re-running side effects
            switch (operation.Kind)
            {
                case BridgeCommandKind.PlayMode:
                case BridgeCommandKind.EditMode:
                    logCapture.Start(operation.Kind);
                    editorModeTransition.Start(operation.Kind, restoredOperation: true);
                    break;
                case BridgeCommandKind.RefreshAssetDatabase:
                case BridgeCommandKind.ReimportAssets:
                    logCapture.Start(operation.Kind);
                    assetImportMonitor.ResumeRestored(operation, operation.Kind);
                    break;
                case BridgeCommandKind.RunTestsEditMode:
                case BridgeCommandKind.RunTestsPlayMode:
                case BridgeCommandKind.RunTestsPlayer:
                    logCapture.Start(operation.Kind);
                    testRunMonitor.ResumeRestored(
                        operation.Kind,
                        RestorePendingTestCompletion(operation)
                    );
                    break;
                case BridgeCommandKind.ProjectSettings:
                    logCapture.Start(operation.Kind);
                    _ = ExecuteProjectSettingsAsync(operation);
                    break;
            }
        }

        BridgeCommandResult? RestorePendingTestCompletion(PendingOperationState operation)
        {
            var checkpoint = OperationPersistence.RestorePendingTestCompletion();
            if (checkpoint?.RequestID == operation.RequestID
                && checkpoint.CommandType == operation.CommandType)
                return checkpoint.Result;

            OperationPersistence.ClearPendingTestCompletion();
            return null;
        }

        void RemoveQueuedOperation(PendingOperationState operation)
        {
            var queuedOperationIndex = queuedOperations.IndexOf(operation);
            if (queuedOperationIndex >= 0)
                queuedOperations.RemoveAt(queuedOperationIndex);

            UpdateSnapshot();
            PumpQueuedCommands();
        }

        async Task CompleteUnhandledCommandExceptionAsync(PendingOperationState operation, Exception exception)
        {
            try
            {
                await CompleteCurrentAsync(
                    new()
                    {
                        outcome = ToolOutcome.Exception,
                        exception = SafeToExceptionInfo(exception),
                        diagnostic = exception.Message,
                    }
                );
            }
            catch (Exception completionException)
            {
                ConduitDiagnostics.Error($"Failed to report unhandled exception for '{operation.CommandType}'.", completionException);
                AbandonActiveOperation(operation);
            }
        }

        void HandleClientDisconnected(int clientId)
        {
            var disconnectedActiveOperation = false;
            var operation = activeOperation;
            // zero marks reconnectable work without retaining a stale connection id
            if (operation?.ClientID == clientId)
            {
                operation.ClientID = 0;
                disconnectedActiveOperation = true;
            }

            foreach (var queuedOperation in queuedOperations)
                if (queuedOperation.ClientID == clientId)
                    queuedOperation.ClientID = 0;

            UpdateSnapshot();

            if (disconnectedActiveOperation && operation != null)
                ConduitDiagnostics.Warn($"MCP client disconnected while '{operation.CommandType}' was still active. Waiting for the same request id to reconnect.");
        }

        bool TryFindQueuedOperation(string requestId, string commandType, out PendingOperationState? operation)
        {
            foreach (var queuedOperation in queuedOperations)
            {
                if (queuedOperation.RequestID != requestId || queuedOperation.CommandType != commandType)
                    continue;

                operation = queuedOperation;
                return true;
            }

            operation = null;
            return false;
        }

        void AbandonActiveOperation(PendingOperationState operation)
        {
            if (!ReferenceEquals(activeOperation, operation))
                return;

            activeOperation = null;
            StopOperationHooks();
            OperationPersistence.ClearActiveOperation();
            UpdateSnapshot();
            PumpQueuedCommands();
        }

        void FinishOperation(PendingOperationState operation, BridgeCommandResult result)
        {
            activeOperation = null;
            UpdateSnapshot();
            if (queuedOperations.Count > 0)
                Volatile.Write(ref pumpRequested, 1);

            result.diagnostic = BridgeExceptionFormatter.NormalizeDiagnostic(result.diagnostic, result.exception?.message);
            var logs = logCapture.Drain(operation.Kind, result.outcome, result.diagnostic, out var discardLogs);
            result.logs = discardLogs ? string.Empty : logs;

            StopOperationHooks();
            OperationPersistence.ClearActiveOperation();
            ConduitToolUsage.CompleteCall(
                operation.CommandType,
                operation.ToolUsageStartedUtcTicks
            );
        }

        void StopOperationHooks()
        {
            editorModeTransition.Stop();
            assetImportMonitor.Stop();
            testRunMonitor.Stop();
            OperationPersistence.ClearPendingTestCompletion();
            ConduitGameViewFocus.Restore();
            ConduitGameViewResolution.RestoreIfInEditMode();
            ConduitGameViewAudio.RestoreIfInEditMode();
        }

        void CheckpointTestCompletion(BridgeCommandResult result)
        {
            if (activeOperation is not { } operation || !BridgeCommandKinds.IsTest(operation.Kind))
                return;

            OperationPersistence.SavePendingTestCompletion(
                new()
                {
                    RequestID = operation.RequestID,
                    CommandType = operation.CommandType,
                    Result = result,
                }
            );
        }

        BridgeExceptionInfo SafeToExceptionInfo(Exception exception)
        {
            try
            {
                return BridgeExceptionFormatter.ToInfo(exception);
            }
            catch (Exception formattingException)
            {
                ConduitDiagnostics.Error("Failed to convert command exception to bridge payload.", formattingException);
                return new()
                {
                    type = exception.GetType().Name,
                    message = exception.Message,
                };
            }
        }

        void PersistPendingResult(string requestId, string commandType, BridgeCommandResult result)
        {
            pendingResult = new()
            {
                RequestID = requestId,
                CommandType = commandType,
                Result = result,
            };
            OperationPersistence.SavePendingResult(pendingResult);
            UpdateSnapshot();
        }

        void ClearPendingResult()
        {
            pendingResult = null;
            OperationPersistence.ClearPendingResult();
            UpdateSnapshot();
        }

        void ClearPendingResult(string requestId, string commandType)
        {
            if (pendingResult?.RequestID != requestId || pendingResult.CommandType != commandType)
                return;

            ClearPendingResult();
        }

        // the protocol has no result-consumed ack; reconnects may need this payload again
        static Task ReplayPendingResultAsync(int clientId, PersistedPendingResultState pendingResult)
            => ConduitConnection.TrySendResultAsync(
                clientId,
                pendingResult.RequestID,
                pendingResult.Result,
                pendingResult.CommandType
            );

        void UpdateSnapshot()
            => Volatile.Write(ref snapshot, ClientWorkSnapshot.Create(activeOperation, queuedOperations, pendingResult != null));
    }
}
