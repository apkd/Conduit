#nullable enable

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static bool IsTestCommand(ParsedBridgeCommandKind kind)
            => kind is ParsedBridgeCommandKind.RunTestsEditMode
                or ParsedBridgeCommandKind.RunTestsPlayMode
                or ParsedBridgeCommandKind.RunTestsPlayer;

        static bool CanRestorePersistedOperation(ParsedBridgeCommandKind kind)
            => kind is ParsedBridgeCommandKind.Play or ParsedBridgeCommandKind.RefreshAssetDatabase
               || IsTestCommand(kind);

        static bool IsStaleRestoredOperation(PendingOperationState? operation, ParsedBridgeCommand command)
            => operation is { is_restored: true }
               && !HasPendingResult()
               && command.Kind switch
               {
                   ParsedBridgeCommandKind.Play                 => false,
                   ParsedBridgeCommandKind.RefreshAssetDatabase => !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                   _ when IsTestCommand(command.Kind)           => !EditorApplication.isPlayingOrWillChangePlaymode,
                   _                                            => true,
               };

        internal static void PersistActiveOperation(PendingOperationState operation, ParsedBridgeCommandKind commandKind)
        {
            if (!CanRestorePersistedOperation(commandKind))
                return;

            SessionState.SetString(
                ActiveOperationStateKey,
                JsonUtility.ToJson(
                    new PersistedOperationState
                    {
                        RequestID = operation.request_id,
                        CommandType = operation.command_type,
                        Target = operation.target,
                        TestFilter = operation.test_filter,
                    }
                )
            );
        }

        internal static void RestorePersistedOperation()
        {
            lock (stateGate)
                if (activeOperation != null)
                    return;

            if (SessionState.GetString(ActiveOperationStateKey, string.Empty) is not { Length: > 0 } payload)
                return;

            PersistedOperationState restoredState;
            try
            {
                restoredState = JsonUtility.FromJson<PersistedOperationState>(payload);
            }
            catch (ArgumentException)
            {
                ClearPersistedActiveOperation();
                return;
            }

            if (restoredState == null || string.IsNullOrWhiteSpace(restoredState.RequestID))
            {
                ClearPersistedActiveOperation();
                return;
            }

            var restoredCommand = ParseIncomingCommand(restoredState.CommandType);
            if (!CanRestorePersistedOperation(restoredCommand.Kind))
            {
                ClearPersistedActiveOperation();
                return;
            }

            lock (stateGate)
            {
                if (activeOperation != null)
                    return;

                activeOperation = new()
                {
                    request_id = restoredState.RequestID,
                    command_type = restoredState.CommandType,
                    client_id = 0,
                    target = restoredState.Target,
                    test_filter = restoredState.TestFilter,
                    args = Array.Empty<string>(),
                    is_acknowledged = true,
                    is_restored = true,
                };

                activeCommand = restoredCommand;
            }

            if (restoredCommand.Kind == ParsedBridgeCommandKind.Play && !TryGetPlayTargetMode(restoredState.Target, out _))
            {
                lock (stateGate)
                {
                    activeOperation = null;
                    activeCommand = default;
                }

                ClearPersistedActiveOperation();
            }
        }

        static void ResumeRestoredOperation()
        {
            PendingOperationState? operation;
            ParsedBridgeCommand command;
            lock (stateGate)
            {
                operation = activeOperation;
                command = activeCommand;
            }

            if (operation is not { is_restored: true })
                return;

            switch (command.Kind)
            {
                case ParsedBridgeCommandKind.Play:
                    InstallPlayModeHooks();
                    ResetPlayModeState();
                    TryAdvancePlayToggle();
                    return;
                case ParsedBridgeCommandKind.RefreshAssetDatabase:
                    InstallReimportHooks();
                    MarkRestoredReimportAsResumed();
                    TryFinishReimport();
                    return;
            }
        }

        static void PersistPendingResult(string requestId, string commandType, BridgeCommandResult result)
        {
            pendingResult = new()
            {
                RequestID = requestId,
                CommandType = commandType,
                Result = result,
            };

            SessionState.SetString(PendingResultStateKey, JsonUtility.ToJson(pendingResult));
        }

        static void RestorePersistedPendingResult()
        {
            if (pendingResult != null)
                return;

            if (SessionState.GetString(PendingResultStateKey, string.Empty) is not { Length: > 0 } payload)
                return;

            try
            {
                pendingResult = JsonUtility.FromJson<PersistedPendingResultState>(payload);
            }
            catch (ArgumentException)
            {
                ClearPendingResult();
                return;
            }

            if (pendingResult?.Result == null || string.IsNullOrWhiteSpace(pendingResult.RequestID))
                ClearPendingResult();
        }

        static bool HasPendingResult() => pendingResult != null;

        internal static void ClearPersistedActiveOperation()
            => SessionState.EraseString(ActiveOperationStateKey);

        static void ClearPendingResult()
        {
            pendingResult = null;
            SessionState.EraseString(PendingResultStateKey);
        }

        static async System.Threading.Tasks.Task ReplayPendingResultAsync(int clientId, PersistedPendingResultState pendingResult)
        {
            /*
             * Intentionally do not clear here. The protocol has no explicit
             * result-consumed ack, so another reconnect for the same request id
             * may still need this cached payload.
             */
            await ConduitConnection.TrySendResultAsync(clientId, pendingResult.RequestID, pendingResult.Result, pendingResult.CommandType);
        }

        [Serializable]
        sealed class PersistedOperationState
        {
            [FormerlySerializedAs("request_id")] public string RequestID = string.Empty;
            [FormerlySerializedAs("command_type")] public string CommandType = string.Empty;
            [FormerlySerializedAs("target")] public string? Target;
            [FormerlySerializedAs("test_filter")] public string? TestFilter;
        }

        [Serializable]
        sealed class PersistedPendingResultState
        {
            [FormerlySerializedAs("request_id")] public string RequestID = string.Empty;
            [FormerlySerializedAs("command_type")] public string CommandType = string.Empty;
            [FormerlySerializedAs("result")] public BridgeCommandResult Result = new();
        }
    }
}
