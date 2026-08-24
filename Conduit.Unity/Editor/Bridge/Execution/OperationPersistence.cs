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

        internal static void SaveActiveOperation(PendingOperationState operation, BridgeCommandKind commandKind)
        {
            if (!CanRestore(commandKind))
                return;

            SessionState.SetString(
                ActiveOperationStateKey,
                JsonUtility.ToJson(
                    new PersistedOperationState
                    {
                        RequestID = operation.RequestID,
                        CommandType = operation.CommandType,
                        Target = operation.Target,
                        Snippet = operation.Snippet,
                        Args = operation.Args,
                        TestFilter = operation.TestFilter,
                        ToolUsageStartedUtcTicks = operation.ToolUsageStartedUtcTicks,
                        ReimportAssetPaths = operation.ReimportAssetPaths,
                        ProjectSettingPrevious = operation.ProjectSettingPrevious,
                    }
                )
            );
        }

        internal static PendingOperationState? RestoreActiveOperation()
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
                RequestID = restoredState.RequestID,
                CommandType = restoredState.CommandType,
                Kind = commandKind,
                ClientID = 0,
                Target = restoredState.Target,
                Snippet = restoredState.Snippet,
                TestFilter = restoredState.TestFilter,
                ToolUsageStartedUtcTicks = restoredState.ToolUsageStartedUtcTicks,
                Args = restoredState.Args ?? Array.Empty<string>(),
                IsAcknowledged = true,
                IsRestored = true,
                ReimportAssetPaths = restoredState.ReimportAssetPaths ?? Array.Empty<string>(),
                ProjectSettingPrevious = restoredState.ProjectSettingPrevious,
            };
        }

        // session state can outlive the editor activity that originally justified recovery
        internal static bool IsStaleRestoredOperation(
            PendingOperationState? operation,
            BridgeCommandKind commandKind,
            bool hasPendingResult,
            bool isAnyTestRunActive)
            => operation is { IsRestored: true }
               && !hasPendingResult
               && commandKind switch
               {
                   _ when BridgeCommandKinds.IsEditorMode(commandKind)  => false,
                   _ when BridgeCommandKinds.IsAssetImport(commandKind) => !EditorApplication.isCompiling && !EditorApplication.isUpdating,
                   _ when BridgeCommandKinds.IsTest(commandKind)        => !isAnyTestRunActive && !EditorApplication.isPlayingOrWillChangePlaymode,
                   _ when commandKind == BridgeCommandKind.ProjectSettings => false,
                   _                                                    => true,
               };

        internal static PersistedPendingResultState? RestorePendingResult()
            => RestoreResult(PendingResultStateKey);

        internal static PersistedPendingResultState? RestorePendingTestCompletion()
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

        internal static void SavePendingResult(PersistedPendingResultState pendingResult)
            => SessionState.SetString(PendingResultStateKey, JsonUtility.ToJson(pendingResult));

        internal static void SavePendingTestCompletion(PersistedPendingResultState pendingResult)
            => SessionState.SetString(PendingTestCompletionStateKey, JsonUtility.ToJson(pendingResult));

        internal static void ClearActiveOperation()
            => SessionState.EraseString(ActiveOperationStateKey);

        internal static void ClearPendingResult()
            => SessionState.EraseString(PendingResultStateKey);

        internal static void ClearPendingTestCompletion()
            => SessionState.EraseString(PendingTestCompletionStateKey);

        // editor-owned asynchronous work can survive connection loss or domain reloads
        internal static bool CanRestore(BridgeCommandKind commandKind)
            => BridgeCommandKinds.IsEditorMode(commandKind)
               || BridgeCommandKinds.IsAssetImport(commandKind)
               || BridgeCommandKinds.IsTest(commandKind)
               || commandKind == BridgeCommandKind.ProjectSettings;

        [Serializable]
        sealed class PersistedOperationState
        {
            public string RequestID = string.Empty;
            public string CommandType = string.Empty;
            public string? Target;
            public string? Snippet;
            public string[] Args = Array.Empty<string>();
            public string? TestFilter;
            public long ToolUsageStartedUtcTicks;
            public string[] ReimportAssetPaths = Array.Empty<string>();
            public string? ProjectSettingPrevious;
        }
    }
}
