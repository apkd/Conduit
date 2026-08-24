#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    sealed partial class CommandScheduler
    {
        // connection callbacks may arrive outside editor update; mutable scheduler state stays pump-owned
        readonly ConcurrentQueue<SchedulerEvent> pendingEvents = new();
        readonly ConcurrentQueue<Action> pendingMainThreadActions = new();
        readonly List<PendingOperationState> queuedOperations = new();
        readonly ToolLogCapture logCapture = new();
        readonly EditorModeTransition editorModeTransition;
        readonly AssetImportMonitor assetImportMonitor;
        readonly UnityTestRunMonitor testRunMonitor;
        // connection liveness checks need lock-free reads without touching mutable operation objects
        ClientWorkSnapshot snapshot = ClientWorkSnapshot.Empty;
        PendingOperationState? activeOperation;
        PersistedPendingResultState? pendingResult;
        int pumpRequested;

        internal CommandScheduler()
        {
            editorModeTransition = new(CompleteCurrentAsync);
            assetImportMonitor = new(CompleteCurrentAsync);
            testRunMonitor = new(logCapture, CompleteCurrentAsync, CheckpointTestCompletion);
        }

        internal string? ActiveCommandType => Volatile.Read(ref snapshot).ActiveCommandType;

        internal void Initialize() => testRunMonitor.EnsureCallbacksRegistered();

        internal bool IsTestRunnerActive() => testRunMonitor.IsAnyTestRunActive();

        internal string? GetActiveTestRunMode() => testRunMonitor.GetActiveTestRunMode();

        internal bool HasOutstandingClientWork(int clientId)
            => Volatile.Read(ref snapshot).HasOutstandingClientWork(clientId);

        internal bool HasReconnectableWorkForAnyClient()
            => Volatile.Read(ref snapshot).HasReconnectableWorkForAnyClient();

        internal void EnqueueConnected()
        {
            pendingEvents.Enqueue(SchedulerEvent.Connected());
            Volatile.Write(ref pumpRequested, 1);
        }

        internal void EnqueueIncomingCommand(int clientId, BridgeMessage message)
        {
            pendingEvents.Enqueue(SchedulerEvent.Command(clientId, message));
            Volatile.Write(ref pumpRequested, 1);
        }

        internal void EnqueueClientDisconnected(int clientId)
        {
            pendingEvents.Enqueue(SchedulerEvent.Disconnected(clientId));
            Volatile.Write(ref pumpRequested, 1);
        }

        internal void Pump()
        {
            if (Volatile.Read(ref pumpRequested) == 0)
                return;
            if (Interlocked.Exchange(ref pumpRequested, 0) == 0)
                return;

            while (pendingEvents.TryDequeue(out var schedulerEvent))
                ProcessEvent(schedulerEvent);

            while (pendingMainThreadActions.TryDequeue(out var action))
                action();

            PumpQueuedCommands();
        }

        internal async Task CompleteCurrentAsync(BridgeCommandResult result)
        {
            var operation = activeOperation;
            if (operation == null)
                return;

            FinishOperation(operation, result);
            // persist before yielding to IPC so a reload cannot erase a completed result
            PersistPendingResult(operation.RequestID, operation.CommandType, result);
            if (await ConduitConnection.TrySendResultAsync(
                    operation.ClientID,
                    operation.RequestID,
                    result,
                    operation.CommandType
                ))
                EnqueueMainThreadAction(
                    () => ClearPendingResult(operation.RequestID, operation.CommandType)
                );
        }

        internal void PrepareForAssemblyReload()
        {
            var operation = activeOperation;
            if (operation == null || OperationPersistence.CanRestore(operation.Kind))
                return;

            var diagnostic = ConduitToolRunner.BuildAssemblyReloadInterruptionDiagnostic(
                operation.CommandType
            );
            var result = new BridgeCommandResult
            {
                outcome = ToolOutcome.Exception,
                diagnostic = diagnostic,
            };
            FinishOperation(operation, result);
            PersistPendingResult(operation.RequestID, operation.CommandType, result);
            ConduitDiagnostics.Warn(diagnostic);
        }

        readonly struct SchedulerEvent
        {
            internal readonly SchedulerEventKind Kind;
            internal readonly int ClientId;
            internal readonly BridgeMessage? Message;

            SchedulerEvent(SchedulerEventKind kind, int clientId, BridgeMessage? message)
            {
                Kind = kind;
                ClientId = clientId;
                Message = message;
            }

            internal static SchedulerEvent Connected()
                => new(SchedulerEventKind.Connected, 0, null);

            internal static SchedulerEvent Command(int clientId, BridgeMessage message)
                => new(SchedulerEventKind.Command, clientId, message);

            internal static SchedulerEvent Disconnected(int clientId)
                => new(SchedulerEventKind.Disconnected, clientId, null);
        }

        enum SchedulerEventKind : byte
        {
            Connected,
            Command,
            Disconnected,
        }
    }
}
