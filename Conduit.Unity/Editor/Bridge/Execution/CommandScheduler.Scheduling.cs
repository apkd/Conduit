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

            if (pendingResult != null)
            {
                OperationPersistence.ClearActiveOperation();
                OperationPersistence.ClearPendingTestCompletion();
                UpdateSnapshot();
                return;
            }

            if (OperationPersistence.RestoreActiveOperation() is not { } restoredOperation)
            {
                OperationPersistence.ClearPendingTestCompletion();
                UpdateSnapshot();
                return;
            }

            activeOperation = restoredOperation;
            UpdateSnapshot();
            ResumeRestoredOperation();
        }

        void HandleCommand(int clientId, BridgeMessage message)
        {
            if (message.request_id is not { Length: > 0 } requestId)
                return;

            if (message.message_type == BridgeMessageTypes.CancelCommand)
            {
                HandleCommandCancellation(clientId, requestId);
                return;
            }

            if (message.command == null)
                return;

            var commandType = message.command.command_type;
            var incomingCommandKind = BridgeCommandKinds.Parse(commandType);
            if (incomingCommandKind == BridgeCommandKind.Status)
            {
                // status should answer immediately even when a long-running editor operation is active
                _ = AcknowledgeAndExecuteStatusAsync(
                    clientId,
                    message.request_id,
                    message.command.track_usage
                        ? ConduitToolUsage.BeginCall(BridgeCommandTypes.Status)
                        : 0L
                );
                return;
            }

            ClearStaleRestoredOperation();

            if (TryReplayPendingResult(clientId, message.request_id, commandType))
                return;

            var pendingOperation = FindOrCreateOperation(clientId, message, incomingCommandKind);
            _ = AcknowledgeQueuedCommandAsync(pendingOperation);
            UpdateSnapshot();
        }

        void HandleCommandCancellation(int clientId, string requestId)
        {
            if (activeOperation is { } active
                && active.RequestID == requestId
                && BridgeCommandKinds.SupportsCancellation(active.Kind))
            {
                active.ClientID = clientId;
                var cancelled = active.Kind == BridgeCommandKind.Record
                    ? RecordTool.CancelWait()
                    : testRunMonitor.Cancel();
                if (!cancelled)
                    ConduitDiagnostics.Warn($"Could not cancel Unity request '{requestId}'.");

                UpdateSnapshot();
                return;
            }

            var queuedOperation = queuedOperations.Find(
                operation => operation.RequestID == requestId
                             && BridgeCommandKinds.SupportsCancellation(operation.Kind)
            );
            if (queuedOperation == null)
                return;

            queuedOperations.Remove(queuedOperation);
            ConduitToolUsage.CompleteCall(
                queuedOperation.CommandType,
                queuedOperation.ToolUsageStartedUtcTicks
            );
            UpdateSnapshot();
            _ = ConduitConnection.TrySendResultAsync(
                clientId,
                requestId,
                queuedOperation.Kind == BridgeCommandKind.Record
                    ? new()
                    {
                        outcome = ToolOutcome.Cancelled,
                        diagnostic = "The recording wait was cancelled; any active recording continues in the background.",
                    }
                    : RunTestsTool.CreateRequestCancelledResult(),
                queuedOperation.CommandType
            );
            PumpQueuedCommands();
        }

        void ClearStaleRestoredOperation()
        {
            if (!OperationPersistence.IsStaleRestoredOperation(
                    activeOperation,
                    activeOperation?.Kind ?? BridgeCommandKind.Unknown,
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
                && active.RequestID == message.request_id
                && active.CommandType == command.command_type)
            {
                active.ClientID = clientId;
                return active;
            }

            if (TryFindQueuedOperation(message.request_id!, command.command_type, out var queuedOperation))
            {
                queuedOperation!.ClientID = clientId;
                return queuedOperation;
            }

            // queueing is Unity-side latency, so timing starts when the editor accepts the request.
            long usageStartedUtcTicks = command.track_usage
                ? ConduitToolUsage.BeginCall(command.command_type)
                : 0L;
            var operation = new PendingOperationState
            {
                RequestID = message.request_id!,
                CommandType = command.command_type,
                Kind = commandKind,
                ClientID = clientId,
                Target = command.target,
                Snippet = command.snippet,
                DisplayName = command.display_name,
                TestFilter = command.test_filter,
                IsAsync = command.@async,
                RebuildCache = command.rebuild_cache,
                ToolUsageStartedUtcTicks = usageStartedUtcTicks,
                Args = command.args ?? Array.Empty<string>(),
                Artifacts = command.artifacts ?? Array.Empty<BridgeArtifact>(),
            };
            queuedOperations.Add(operation);
            return operation;
        }

        async Task AcknowledgeAndExecuteStatusAsync(
            int clientId,
            string requestId,
            long usageStartedUtcTicks
        )
        {
            if (!await ConduitConnection.TrySendCommandStartedAsync(clientId, requestId, BridgeCommandTypes.Status))
                return;

            EnqueueMainThreadAction(
                () => _ = ExecuteStatusAsync(clientId, requestId, usageStartedUtcTicks)
            );
        }

        async Task AcknowledgeQueuedCommandAsync(PendingOperationState operation)
        {
            if (operation.IsAcknowledged)
            {
                if (operation.ClientID > 0)
                    await ConduitConnection.TrySendCommandStartedAsync(operation.ClientID, operation.RequestID, operation.CommandType);

                return;
            }

            if (operation.ClientID <= 0)
                return;

            var acknowledged = await ConduitConnection.TrySendCommandStartedAsync(
                operation.ClientID,
                operation.RequestID,
                operation.CommandType
            );
            EnqueueMainThreadAction(
                () => CompleteAcknowledgement(operation, acknowledged)
            );
        }

        void EnqueueMainThreadAction(Action action)
        {
            pendingMainThreadActions.Enqueue(action);
            Volatile.Write(ref pumpRequested, 1);
        }

        void CompleteAcknowledgement(PendingOperationState operation, bool acknowledged)
        {
            if (!acknowledged)
            {
                RemoveQueuedOperation(operation);
                return;
            }

            // the send crosses async boundaries; the operation may have completed or been dropped
            if (ReferenceEquals(activeOperation, operation) || queuedOperations.Contains(operation))
                operation.IsAcknowledged = true;
        }

        void PumpQueuedCommands()
        {
            if (activeOperation != null || queuedOperations.Count == 0)
                return;

            var operation = queuedOperations[0];
            if (!operation.IsAcknowledged)
                return;

            queuedOperations.RemoveAt(0);
            activeOperation = operation;
            operation.Kind = BridgeCommandKinds.Parse(operation.CommandType);
            // persist before side effects so domain reloads can resume editor-owned work
            OperationPersistence.SaveActiveOperation(operation, operation.Kind);
            UpdateSnapshot();
            _ = ExecuteAcceptedCommandAsync(operation, operation.Kind);
        }

    }
}
