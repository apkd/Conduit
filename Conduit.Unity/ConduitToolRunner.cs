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
    static partial class ConduitToolRunner
    {
        static readonly CommandScheduler scheduler = new();
        internal const int ReimportIdleSettleUpdates = AssetImportMonitor.IdleSettleUpdates;
        static bool initialized;

        public static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            ConduitOpenSceneDiskChangeGuard.Initialize();
            execute_code.Initialize();
            scheduler.Initialize();
        }

        public static Task OnConnectedAsync()
        {
            scheduler.EnqueueConnected();
            return Task.CompletedTask;
        }

        internal static string? GetActiveCommandType()
            => scheduler.ActiveCommandType;

        internal static bool IsTestRunnerActive()
            => scheduler.IsTestRunnerActive();

        internal static string? GetActiveTestRunMode()
            => scheduler.GetActiveTestRunMode();

        internal static bool HasOutstandingClientWork(int clientId)
            => scheduler.HasOutstandingClientWork(clientId);

        internal static bool HasReconnectableWorkForAnyClient()
            => scheduler.HasReconnectableWorkForAnyClient();

        internal static void PumpQueuedCommands()
        {
            Initialize();
            scheduler.Pump();
        }

        public static void HandleIncomingCommand(int clientId, BridgeMessage message)
        {
            Initialize();
            scheduler.EnqueueIncomingCommand(clientId, message);
        }

        internal static void HandleClientDisconnected(int clientId)
            => scheduler.EnqueueClientDisconnected(clientId);

        internal static BridgeCommandKind ParseIncomingCommand(string commandType)
            => BridgeCommandKinds.Parse(commandType);

        static Task CompleteCurrentAsync(BridgeCommandResult result)
            => scheduler.CompleteCurrentAsync(result);

        static BridgeExceptionInfo ToExceptionInfo(Exception exception)
            => ConduitUtility.ToExceptionInfo(exception);

        internal static void ClearPersistedActiveOperation()
            => OperationPersistence.ClearActiveOperation();

        internal static bool ShouldWaitForTestRunCompletion(
            bool isTestRunnerActive,
            bool isCompiling,
            bool isUpdating,
            bool isPlaying,
            bool isPlayingOrWillChangePlaymode)
            => UnityTestRunMonitor.ShouldWaitForCompletion(
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
            => UnityTestRunMonitor.ShouldWaitForCompletion(
                isTestRunnerActive,
                isCompiling,
                isUpdating,
                isPlaying,
                isPlayingOrWillChangePlaymode,
                completeDespiteStuckTestRunner
            );

        internal static bool ShouldWaitToEnterPlayMode(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => EditorModeTransition.ShouldWaitToEnterPlayMode(isCompiling, isUpdating, isPlayingOrWillChangePlaymode);

        internal static bool ShouldFailEnterPlayForCompileErrors(bool scriptCompilationFailed)
            => EditorModeTransition.ShouldFailEnterPlayForCompileErrors(scriptCompilationFailed);

        internal static string BuildEnterPlayBusyDiagnostic(bool isCompiling, bool isUpdating, bool isPlayingOrWillChangePlaymode)
            => EditorModeTransition.BuildEnterPlayBusyDiagnostic(isCompiling, isUpdating, isPlayingOrWillChangePlaymode);

        internal static string BuildEnterPlayCompileErrorDiagnostic()
            => EditorModeTransition.BuildEnterPlayCompileErrorDiagnostic();

        internal static string BuildPlayCompletionDiagnostic(bool targetPlayMode, bool changedMode, bool isPaused)
            => EditorModeTransition.BuildCompletionDiagnostic(targetPlayMode, changedMode, isPaused);

        internal static bool ShouldBlockReimportForPlayMode(bool isPlaying)
            => AssetImportMonitor.ShouldBlockForPlayMode(isPlaying);

        internal static string BuildReimportPlayModeDiagnostic(string commandType = BridgeCommandTypes.RefreshAssetDatabase)
            => AssetImportMonitor.BuildPlayModeDiagnostic(commandType);

        internal static bool ShouldWaitForReimportIdle(bool refreshReturned, bool isCompiling, bool isUpdating, int idleUpdateCount)
            => AssetImportMonitor.ShouldWaitForIdle(refreshReturned, isCompiling, isUpdating, idleUpdateCount);

        internal static string FormatReimportedAssetFilenames(string? assetPathPayload)
            => AssetImportMonitor.FormatReimportedAssetFilenames(assetPathPayload);

        internal static string BuildRestoredReimportCompileErrorDiagnostic()
            => AssetImportMonitor.BuildRestoredCompileErrorDiagnostic();

        internal static string? TrimCommonLogTail(string? simplifiedStackTrace)
            => ToolLogCapture.TrimCommonTail(simplifiedStackTrace);

        internal static string? CleanCapturedLogStack(BridgeCommandKind commandKind, string? stackTrace, LogType logType)
            => ToolLogCapture.CleanCapturedStackTrace(commandKind, stackTrace, logType);

        internal static string FormatCapturedLogEntryForTest(string message, string? stackTrace)
            => ToolLogCapture.FormatCapturedLogEntryForTest(message, stackTrace);

        internal static bool ShouldOmitDiagnosticLogEntry(string message, string? diagnostic)
            => ToolLogCapture.ShouldOmitDiagnosticLogEntry(message, diagnostic);

        internal static bool ShouldSuppressCapturedLogEntry(string message)
            => ToolLogCapture.ShouldSuppressCapturedLogEntry(message);

        internal static string NormalizeCapturedLogMessage(string message)
            => ToolLogCapture.NormalizeCapturedLogMessage(message);

        sealed class CommandScheduler
        {
            // connection callbacks may arrive outside editor update; mutable scheduler state stays pump-owned
            readonly ConcurrentQueue<SchedulerEvent> pendingEvents = new();
            readonly List<PendingOperationState> queuedOperations = new();
            readonly ToolLogCapture logCapture = new();
            readonly EditorModeTransition editorModeTransition;
            readonly AssetImportMonitor assetImportMonitor;
            readonly UnityTestRunMonitor testRunMonitor;
            // connection liveness checks need lock-free reads without touching mutable operation objects
            ClientWorkSnapshot snapshot = ClientWorkSnapshot.Empty;
            PendingOperationState? activeOperation;
            PersistedPendingResultState? pendingResult;

            public CommandScheduler()
            {
                editorModeTransition = new(CompleteCurrentAsync);
                assetImportMonitor = new(CompleteCurrentAsync);
                testRunMonitor = new(logCapture, CompleteCurrentAsync);
                UpdateSnapshot();
            }

            public string? ActiveCommandType => Volatile.Read(ref snapshot).ActiveCommandType;

            public void Initialize() => testRunMonitor.EnsureCallbacksRegistered();

            public bool IsTestRunnerActive() => testRunMonitor.IsAnyTestRunActive();

            public string? GetActiveTestRunMode() => testRunMonitor.GetActiveTestRunMode();

            public bool HasOutstandingClientWork(int clientId)
                => Volatile.Read(ref snapshot).HasOutstandingClientWork(clientId);

            public bool HasReconnectableWorkForAnyClient()
                => Volatile.Read(ref snapshot).HasReconnectableWorkForAnyClient();

            public void EnqueueConnected()
                => pendingEvents.Enqueue(SchedulerEvent.Connected());

            public void EnqueueIncomingCommand(int clientId, BridgeMessage message)
                => pendingEvents.Enqueue(SchedulerEvent.Command(clientId, message));

            public void EnqueueClientDisconnected(int clientId)
                => pendingEvents.Enqueue(SchedulerEvent.Disconnected(clientId));

            public void Pump()
            {
                while (pendingEvents.TryDequeue(out var schedulerEvent))
                    ProcessEvent(schedulerEvent);

                PumpQueuedCommands();
            }

            public async Task CompleteCurrentAsync(BridgeCommandResult result)
            {
                var operation = activeOperation;
                if (operation == null)
                    return;

                var commandKind = operation.kind;
                activeOperation = null;
                UpdateSnapshot();

                result.diagnostic = ConduitUtility.NormalizeDiagnostic(result.diagnostic, result.exception?.message);
                var logs = logCapture.Drain(commandKind, result.outcome, result.diagnostic, out var discardLogs);
                result.logs = discardLogs ? string.Empty : logs;

                StopOperationHooks();
                OperationPersistence.ClearActiveOperation();

                if (await ConduitConnection.TrySendResultAsync(operation.client_id, operation.request_id, result, operation.command_type))
                {
                    ClearPendingResult();
                    PumpQueuedCommands();
                    return;
                }

                PersistPendingResult(operation.request_id, operation.command_type, result);
                PumpQueuedCommands();
            }

            void ProcessEvent(SchedulerEvent schedulerEvent)
            {
                switch (schedulerEvent.Kind)
                {
                    case SchedulerEventKind.Connected:
                        HandleConnected();
                        break;
                    case SchedulerEventKind.Command:
                        if (schedulerEvent.Message != null)
                            HandleCommand(schedulerEvent.ClientId, schedulerEvent.Message);

                        break;
                    case SchedulerEventKind.Disconnected:
                        HandleClientDisconnected(schedulerEvent.ClientId);
                        break;
                }
            }

            void HandleConnected()
            {
                pendingResult ??= OperationPersistence.RestorePendingResult();
                if (activeOperation != null)
                {
                    UpdateSnapshot();
                    return;
                }

                if (OperationPersistence.RestoreActiveOperation() is not { } restoredOperation)
                {
                    UpdateSnapshot();
                    return;
                }

                activeOperation = restoredOperation;
                UpdateSnapshot();
                ResumeRestoredOperation();
            }

            void HandleCommand(int clientId, BridgeMessage message)
            {
                if (message.command == null || message.request_id is not { Length: > 0 })
                    return;

                var commandType = message.command.command_type;
                var incomingCommandKind = BridgeCommandKinds.Parse(commandType);
                if (incomingCommandKind == BridgeCommandKind.Status)
                {
                    // status should answer immediately even when a long-running editor operation is active
                    _ = AcknowledgeAndExecuteStatusAsync(clientId, message.request_id);
                    return;
                }

                ClearStaleRestoredOperation();

                if (TryReplayPendingResult(clientId, message.request_id, commandType))
                    return;

                var pendingOperation = FindOrCreateOperation(clientId, message, incomingCommandKind);
                _ = AcknowledgeQueuedCommandAsync(pendingOperation);
                UpdateSnapshot();
            }

            void ClearStaleRestoredOperation()
            {
                if (!OperationPersistence.IsStaleRestoredOperation(
                        activeOperation,
                        activeOperation?.kind ?? BridgeCommandKind.Unknown,
                        pendingResult != null,
                        testRunMonitor.IsAnyTestRunActive()
                    ))
                    return;

                activeOperation = null;
                StopOperationHooks();
                OperationPersistence.ClearActiveOperation();
                UpdateSnapshot();
            }

            bool TryReplayPendingResult(int clientId, string requestId, string commandType)
            {
                var replayablePendingResult = pendingResult;
                if (replayablePendingResult == null)
                    return false;

                // completed side effects stay replayable until the client asks for a different request
                if (replayablePendingResult.RequestID == requestId
                    && replayablePendingResult.CommandType == commandType)
                {
                    _ = ReplayPendingResultAsync(clientId, replayablePendingResult);
                    return true;
                }

                ClearPendingResult();
                return false;
            }

            PendingOperationState FindOrCreateOperation(int clientId, BridgeMessage message, BridgeCommandKind commandKind)
            {
                var command = message.command!;
                // reconnects reclaim queued or active work by sending the same request id
                if (activeOperation is { } active
                    && active.request_id == message.request_id
                    && active.command_type == command.command_type)
                {
                    active.client_id = clientId;
                    return active;
                }

                if (TryFindQueuedOperation(message.request_id!, command.command_type, out var queuedOperation))
                {
                    queuedOperation!.client_id = clientId;
                    return queuedOperation;
                }

                var operation = new PendingOperationState
                {
                    request_id = message.request_id!,
                    command_type = command.command_type,
                    kind = commandKind,
                    client_id = clientId,
                    target = command.target,
                    snippet = command.snippet,
                    test_filter = command.test_filter,
                    @async = command.@async,
                    rebuild_cache = command.rebuild_cache,
                    args = command.args ?? Array.Empty<string>(),
                };
                queuedOperations.Add(operation);
                return operation;
            }

            async Task AcknowledgeAndExecuteStatusAsync(int clientId, string requestId)
            {
                if (!await ConduitConnection.TrySendCommandStartedAsync(clientId, requestId, BridgeCommandTypes.Status))
                    return;

                await ExecuteStatusAsync(clientId, requestId);
            }

            async Task AcknowledgeQueuedCommandAsync(PendingOperationState operation)
            {
                if (operation.is_acknowledged)
                {
                    if (operation.client_id > 0)
                        await ConduitConnection.TrySendCommandStartedAsync(operation.client_id, operation.request_id, operation.command_type);

                    return;
                }

                if (operation.client_id <= 0)
                    return;

                if (!await ConduitConnection.TrySendCommandStartedAsync(operation.client_id, operation.request_id, operation.command_type))
                {
                    RemoveQueuedOperation(operation);
                    return;
                }

                // the send crosses async boundaries; the operation may have completed or been dropped
                if (ReferenceEquals(activeOperation, operation) || queuedOperations.Contains(operation))
                {
                    operation.is_acknowledged = true;
                    UpdateSnapshot();
                }

                PumpQueuedCommands();
            }

            void PumpQueuedCommands()
            {
                if (activeOperation != null || queuedOperations.Count == 0)
                    return;

                var operation = queuedOperations[0];
                if (!operation.is_acknowledged)
                    return;

                queuedOperations.RemoveAt(0);
                activeOperation = operation;
                operation.kind = BridgeCommandKinds.Parse(operation.command_type);
                // persist before side effects so domain reloads can resume editor-owned work
                OperationPersistence.SaveActiveOperation(operation, operation.kind);
                UpdateSnapshot();
                _ = ExecuteAcceptedCommandAsync(operation, operation.kind);
            }

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
                            editorModeTransition.Start(commandKind, operation.is_restored);
                            break;
                        case BridgeCommandKind.Screenshot:
                            await ExecuteScreenshotAsync(operation);
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
                        case BridgeCommandKind.ViewBurstAsm:
                            await ExecuteViewBurstAsmAsync(operation);
                            break;
                        case BridgeCommandKind.Reflect:
                            await ExecuteReflectAsync(operation);
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
                        default:
                            await CompleteCurrentAsync(
                                new()
                                {
                                    outcome = ToolOutcome.Exception,
                                    diagnostic = $"Unsupported command '{operation.command_type}'.",
                                }
                            );

                            break;
                    }
                }
                catch (Exception exception)
                {
                    ConduitDiagnostics.Error($"Unhandled exception while executing '{operation.command_type}'.", exception);
                    await CompleteUnhandledCommandExceptionAsync(operation, exception);
                }
            }

            void ResumeRestoredOperation()
            {
                if (activeOperation is not { is_restored: true } operation)
                    return;

                // recovery observes editor-owned work already in progress instead of re-running side effects
                switch (operation.kind)
                {
                    case BridgeCommandKind.PlayMode:
                    case BridgeCommandKind.EditMode:
                        logCapture.Start(operation.kind);
                        editorModeTransition.Start(operation.kind, restoredOperation: true);
                        break;
                    case BridgeCommandKind.RefreshAssetDatabase:
                    case BridgeCommandKind.ReimportAssets:
                        logCapture.Start(operation.kind);
                        assetImportMonitor.ResumeRestored(operation, operation.kind);
                        break;
                    case BridgeCommandKind.RunTestsEditMode:
                    case BridgeCommandKind.RunTestsPlayMode:
                    case BridgeCommandKind.RunTestsPlayer:
                        logCapture.Start(operation.kind);
                        testRunMonitor.ResumeRestored(operation.kind);
                        break;
                }
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
                    ConduitDiagnostics.Error($"Failed to report unhandled exception for '{operation.command_type}'.", completionException);
                    AbandonActiveOperation(operation);
                }
            }

            void HandleClientDisconnected(int clientId)
            {
                var disconnectedActiveOperation = false;
                var operation = activeOperation;
                // zero marks reconnectable work without retaining a stale connection id
                if (operation?.client_id == clientId)
                {
                    operation.client_id = 0;
                    disconnectedActiveOperation = true;
                }

                foreach (var queuedOperation in queuedOperations)
                    if (queuedOperation.client_id == clientId)
                        queuedOperation.client_id = 0;

                UpdateSnapshot();

                if (disconnectedActiveOperation && operation != null)
                    ConduitDiagnostics.Warn($"MCP client disconnected while '{operation.command_type}' was still active. Waiting for the same request id to reconnect.");
            }

            bool TryFindQueuedOperation(string requestId, string commandType, out PendingOperationState? operation)
            {
                foreach (var queuedOperation in queuedOperations)
                {
                    if (queuedOperation.request_id != requestId || queuedOperation.command_type != commandType)
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

            void StopOperationHooks()
            {
                editorModeTransition.Stop();
                assetImportMonitor.Stop();
                testRunMonitor.Stop();
            }

            BridgeExceptionInfo SafeToExceptionInfo(Exception exception)
            {
                try
                {
                    return ToExceptionInfo(exception);
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

            static async Task ReplayPendingResultAsync(int clientId, PersistedPendingResultState pendingResult)
            {
                // the protocol has no result-consumed ack; reconnects may need this payload again
                await ConduitConnection.TrySendResultAsync(
                    clientId,
                    pendingResult.RequestID,
                    pendingResult.Result,
                    pendingResult.CommandType
                );
            }

            void UpdateSnapshot()
                => Volatile.Write(ref snapshot, ClientWorkSnapshot.Create(activeOperation, queuedOperations, pendingResult != null));
        }

        readonly struct SchedulerEvent
        {
            public readonly SchedulerEventKind Kind;
            public readonly int ClientId;
            public readonly BridgeMessage? Message;

            SchedulerEvent(SchedulerEventKind kind, int clientId, BridgeMessage? message)
            {
                Kind = kind;
                ClientId = clientId;
                Message = message;
            }

            public static SchedulerEvent Connected()
                => new(SchedulerEventKind.Connected, 0, null);

            public static SchedulerEvent Command(int clientId, BridgeMessage message)
                => new(SchedulerEventKind.Command, clientId, message);

            public static SchedulerEvent Disconnected(int clientId)
                => new(SchedulerEventKind.Disconnected, clientId, null);
        }

        enum SchedulerEventKind : byte
        {
            Connected,
            Command,
            Disconnected,
        }
    }

    sealed class ClientWorkSnapshot
    {
        public static readonly ClientWorkSnapshot Empty = new(null, -1, Array.Empty<int>(), hasPendingResult: false);
        readonly int activeClientId;
        readonly int[] queuedClientIds;
        readonly bool hasPendingResult;

        ClientWorkSnapshot(string? activeCommandType, int activeClientId, int[] queuedClientIds, bool hasPendingResult)
        {
            ActiveCommandType = activeCommandType;
            this.activeClientId = activeClientId;
            this.queuedClientIds = queuedClientIds;
            this.hasPendingResult = hasPendingResult;
        }

        public string? ActiveCommandType { get; }

        public static ClientWorkSnapshot Create(
            PendingOperationState? activeOperation,
            List<PendingOperationState> queuedOperations,
            bool hasPendingResult)
        {
            var queuedClientIds = queuedOperations.Count == 0
                ? Array.Empty<int>()
                : new int[queuedOperations.Count];
            for (var index = 0; index < queuedOperations.Count; index++)
                queuedClientIds[index] = queuedOperations[index].client_id;

            return new(
                activeOperation?.command_type,
                activeOperation?.client_id ?? -1,
                queuedClientIds,
                hasPendingResult
            );
        }

        public bool HasOutstandingClientWork(int clientId)
        {
            if (clientId <= 0)
                return false;

            if (activeClientId == clientId)
                return true;

            foreach (var queuedClientId in queuedClientIds)
                if (queuedClientId == clientId)
                    return true;

            return false;
        }

        public bool HasReconnectableWorkForAnyClient()
        {
            if (ActiveCommandType != null && activeClientId == 0)
                return true;

            foreach (var queuedClientId in queuedClientIds)
                if (queuedClientId == 0)
                    return true;

            return hasPendingResult;
        }
    }
}
