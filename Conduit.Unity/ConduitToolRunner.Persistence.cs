#nullable enable

using System;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class OperationPersistence
    {
        const string ActiveOperationStateKey = "Conduit.ActiveOperation";
        const string PendingResultStateKey = "Conduit.PendingResult";
        // test completion can precede the idle window in which the result is safe to send
        const string PendingTestCompletionStateKey = "Conduit.PendingTestCompletion";

        public static void SaveActiveOperation(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            if (!CanRestore(commandKind))
                return;

            SessionState.SetString(
                ActiveOperationStateKey,
                JsonUtility.ToJson(
                    new PersistedOperationState
                    {
                        RequestID = operation.request_id,
                        CommandType = operation.command_type,
                        Target = operation.target,
                        Snippet = operation.snippet,
                        TestFilter = operation.test_filter,
                        ToolUsageStartedUtcTicks = operation.tool_usage_started_utc_ticks,
                        ReimportAssetPaths = operation.reimport_asset_paths,
                    }
                )
            );
        }

        public static PendingOperationState? RestoreActiveOperation()
        {
            if (SessionState.GetString(ActiveOperationStateKey, string.Empty) is not { Length: > 0 } payload)
                return null;

            PersistedOperationState restoredState;
            try
            {
                restoredState = JsonUtility.FromJson<PersistedOperationState>(payload);
            }
            catch (ArgumentException)
            {
                ClearActiveOperation();
                return null;
            }

            if (restoredState == null || string.IsNullOrWhiteSpace(restoredState.RequestID))
            {
                ClearActiveOperation();
                return null;
            }

            var commandKind = BridgeCommandKinds.Parse(restoredState.CommandType);
            if (!CanRestore(commandKind))
            {
                ClearActiveOperation();
                return null;
            }

            return new()
            {
                request_id = restoredState.RequestID,
                command_type = restoredState.CommandType,
                kind = commandKind,
                client_id = 0,
                target = restoredState.Target,
                snippet = restoredState.Snippet,
                test_filter = restoredState.TestFilter,
                tool_usage_started_utc_ticks = restoredState.ToolUsageStartedUtcTicks,
                args = Array.Empty<string>(),
                is_acknowledged = true,
                is_restored = true,
                reimport_asset_paths = restoredState.ReimportAssetPaths ?? Array.Empty<string>(),
            };
        }

        // session state can outlive the editor activity that originally justified recovery
        public static bool IsStaleRestoredOperation(
            PendingOperationState? operation,
            BridgeCommandKind commandKind,
            bool hasPendingResult,
            bool isAnyTestRunActive)
            => operation is { is_restored: true }
               && !hasPendingResult
               && commandKind switch
               {
                   _ when BridgeCommandKinds.IsEditorMode(commandKind)  => false,
                   _ when BridgeCommandKinds.IsAssetImport(commandKind) => !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                   _ when BridgeCommandKinds.IsTest(commandKind)        => !isAnyTestRunActive && !EditorApplication.isPlayingOrWillChangePlaymode,
                   _                                                    => true,
               };

        public static PersistedPendingResultState? RestorePendingResult()
            => RestoreResult(PendingResultStateKey);

        public static PersistedPendingResultState? RestorePendingTestCompletion()
            => RestoreResult(PendingTestCompletionStateKey);

        static PersistedPendingResultState? RestoreResult(string stateKey)
        {
            if (SessionState.GetString(stateKey, string.Empty) is not { Length: > 0 } payload)
                return null;

            PersistedPendingResultState? pendingResult;
            try
            {
                pendingResult = JsonUtility.FromJson<PersistedPendingResultState>(payload);
            }
            catch (ArgumentException)
            {
                SessionState.EraseString(stateKey);
                return null;
            }

            if (pendingResult?.Result == null || string.IsNullOrWhiteSpace(pendingResult.RequestID))
            {
                SessionState.EraseString(stateKey);
                return null;
            }

            return pendingResult;
        }

        public static void SavePendingResult(PersistedPendingResultState pendingResult)
            => SessionState.SetString(PendingResultStateKey, JsonUtility.ToJson(pendingResult));

        public static void SavePendingTestCompletion(PersistedPendingResultState pendingResult)
            => SessionState.SetString(PendingTestCompletionStateKey, JsonUtility.ToJson(pendingResult));

        public static void ClearActiveOperation()
            => SessionState.EraseString(ActiveOperationStateKey);

        public static void ClearPendingResult()
            => SessionState.EraseString(PendingResultStateKey);

        public static void ClearPendingTestCompletion()
            => SessionState.EraseString(PendingTestCompletionStateKey);

        // editor-owned asynchronous work can survive connection loss or domain reloads
        internal static bool CanRestore(BridgeCommandKind commandKind)
            => BridgeCommandKinds.IsEditorMode(commandKind)
               || BridgeCommandKinds.IsAssetImport(commandKind)
               || BridgeCommandKinds.IsTest(commandKind);

        [Serializable]
        sealed class PersistedOperationState
        {
            public string RequestID = string.Empty;
            public string CommandType = string.Empty;
            public string? Target;
            public string? Snippet;
            public string? TestFilter;
            public long ToolUsageStartedUtcTicks;
            public string[] ReimportAssetPaths = Array.Empty<string>();
        }
    }

    [Serializable]
    sealed class PersistedPendingResultState
    {
        public string RequestID = string.Empty;
        public string CommandType = string.Empty;
        public BridgeCommandResult Result = new();
    }
}
