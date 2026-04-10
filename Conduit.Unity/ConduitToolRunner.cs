#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.Serialization;
using Assembly = System.Reflection.Assembly;

namespace Conduit
{
    static partial class ConduitToolRunner
    {
        static readonly object stateGate = new();
        static readonly StringBuilder compilerMessageBuffer = new();
        static readonly CapturedLogTarget commandLogTarget = new();
        static readonly CapturedLogTarget testRunLogTarget = new();
        static readonly Dictionary<string, CapturedLogTarget> activeTestLogTargets = new(StringComparer.Ordinal);
        static readonly List<CapturedLogTarget> failedTestLogTargets = new();
        static readonly List<string> activeTestScopes = new();
        static readonly Stack<CapturedLogTarget> recycledTestLogTargets = new();
        static readonly Dictionary<LogSignature, int> capturedLogEntryIndexes = new(LogSignatureComparer.Instance);
        static readonly List<CapturedLogEntry> capturedLogEntries = new();
        internal static readonly List<PendingOperationState> queuedOperations = new();
        const string ActiveOperationStateKey = "Conduit.ActiveOperation";
        const string PendingResultStateKey = "Conduit.PendingResult";
        const string PlayTargetEditMode = "edit";
        const string PlayTargetPlayMode = "play";
        static readonly TestRunCallbacks testCallbacks = new();
        static readonly TimeSpan enterPlayModeBusyWaitTimeout = TimeSpan.FromSeconds(1);
        internal static PendingOperationState? activeOperation;
        static PersistedPendingResultState? pendingResult;
        internal static ParsedBridgeCommand activeCommand;
        static bool initialized;
        static bool logCaptureHooked;
        static bool reimportHooksInstalled;
        static bool playModeHooksInstalled;
        static bool reimportRefreshReturned;
        static bool reimportSawImportedScripts;
        static bool reimportObservedCompilation;
        static double enterPlayModeBusyWaitDeadline;
        static bool enterPlayModeRequested;
        static bool discardCapturedLogsOnCompletion;
        static bool testCallbacksRegistered;
        static TestRunnerApi? testRunnerApi;

        internal enum ParsedBridgeCommandKind : byte
        {
            Unknown,
            Status,
            Play,
            Screenshot,
            GetDependencies,
            FindReferencesTo,
            FindMissingScripts,
            Show,
            Search,
            ToJson,
            FromJsonOverwrite,
            SaveScenes,
            DiscardScenes,
            RefreshAssetDatabase,
            ExecuteCode,
            RunTestsEditMode,
            RunTestsPlayMode,
            RunTestsPlayer,
        }

        internal struct ParsedBridgeCommand
        {
            public ParsedBridgeCommandKind Kind;
        }

        public static void Initialize()
        {
            if (initialized)
                return;

            initialized = true;
            execute_code.Initialize();
            EnsureTestCallbacksRegistered();
        }

        public static Task OnConnectedAsync()
        {
            Initialize();
            RestorePersistedPendingResult();
            RestorePersistedOperation();
            ResumeRestoredOperation();
            return Task.CompletedTask;
        }

        internal static string? GetActiveCommandType()
        {
            lock (stateGate)
                return activeOperation?.command_type;
        }

        internal static bool HasOutstandingClientWork(int clientId)
        {
            if (clientId <= 0)
                return false;

            lock (stateGate)
            {
                if (activeOperation?.client_id == clientId)
                    return true;

                foreach (var queuedOperation in queuedOperations)
                    if (queuedOperation.client_id == clientId)
                        return true;
            }

            return false;
        }

        internal static bool HasReconnectableWorkForAnyClient()
        {
            lock (stateGate)
            {
                if (activeOperation?.client_id == 0)
                    return true;

                foreach (var queuedOperation in queuedOperations)
                    if (queuedOperation.client_id == 0)
                        return true;

                return pendingResult != null;
            }
        }

        internal static void PumpQueuedCommands()
        {
            PendingOperationState? pendingOperation;
            ParsedBridgeCommand incomingCommand;
            lock (stateGate)
            {
                if (activeOperation != null || queuedOperations.Count == 0)
                    return;

                pendingOperation = queuedOperations[0];
                if (!pendingOperation.is_acknowledged)
                    return;

                queuedOperations.RemoveAt(0);
                activeOperation = pendingOperation;
                incomingCommand = ParseIncomingCommand(pendingOperation.command_type);
                activeCommand = incomingCommand;
                PersistActiveOperation(pendingOperation, incomingCommand.Kind);
            }

            _ = ExecuteAcceptedCommandAsync(pendingOperation, incomingCommand);
        }

        public static void HandleIncomingCommand(int clientId, BridgeMessage message)
        {
            Initialize();

            if (message.command == null || message.request_id is not { Length: > 0 })
                return;

            var incomingCommand = ParseIncomingCommand(message.command.command_type);
            if (incomingCommand.Kind == ParsedBridgeCommandKind.Status)
            {
                _ = AcknowledgeAndExecuteStatusAsync(clientId, message.request_id);
                return;
            }

            PendingOperationState? pendingOperation = null;
            PersistedPendingResultState? replayablePendingResult = null;
            var shouldAcknowledge = false;
            var shouldReplayPendingResult = false;
            lock (stateGate)
            {
                if (IsStaleRestoredOperation(ConduitToolRunner.activeOperation, activeCommand))
                {
                    ConduitToolRunner.activeOperation = null;
                    activeCommand = default;
                    ClearPersistedActiveOperation();
                }

                replayablePendingResult = ConduitToolRunner.pendingResult;
                if (replayablePendingResult != null)
                {
                    // Reconnect retries must observe the same terminal payload for the same request id.
                    // We keep the cached result until the client moves on because pipe writes are not
                    // an end-to-end delivery acknowledgement.
                    if (replayablePendingResult.RequestID == message.request_id
                        && replayablePendingResult.CommandType == message.command.command_type)
                    {
                        shouldReplayPendingResult = true;
                    }
                    else
                    {
                        ClearPendingResult();
                    }
                }

                if (shouldReplayPendingResult)
                    pendingOperation = null;
                else if (ConduitToolRunner.activeOperation is { } activeOperation
                         && activeOperation.request_id == message.request_id
                         && activeOperation.command_type == message.command.command_type)
                {
                    activeOperation.client_id = clientId;
                    pendingOperation = activeOperation;
                    shouldAcknowledge = true;
                }
                else if (TryFindQueuedOperation(message.request_id, message.command.command_type, out pendingOperation))
                {
                    pendingOperation!.client_id = clientId;
                    shouldAcknowledge = true;
                }
                else
                {
                    pendingOperation = new()
                    {
                        request_id = message.request_id,
                        command_type = message.command.command_type,
                        client_id = clientId,
                        target = incomingCommand.Kind == ParsedBridgeCommandKind.Play
                            ? GetPlayTarget(EditorApplication.isPlaying)
                            : message.command.target,
                        snippet = message.command.snippet,
                        test_filter = message.command.test_filter,
                        rebuild_cache = message.command.rebuild_cache,
                        args = message.command.args ?? Array.Empty<string>(),
                    };
                    queuedOperations.Add(pendingOperation);
                    shouldAcknowledge = true;
                }
            }

            if (shouldReplayPendingResult && replayablePendingResult != null)
            {
                _ = ReplayPendingResultAsync(clientId, replayablePendingResult);
                return;
            }

            if (shouldAcknowledge && pendingOperation != null)
                _ = AcknowledgeQueuedCommandAsync(pendingOperation);
        }

        static async Task AcknowledgeAndExecuteStatusAsync(int clientId, string requestId)
        {
            if (!await ConduitConnection.TrySendCommandStartedAsync(clientId, requestId, BridgeCommandTypes.Status))
                return;

            await ExecuteStatusAsync(clientId, requestId);
        }

        static async Task AcknowledgeQueuedCommandAsync(PendingOperationState pendingOperation)
        {
            if (pendingOperation.is_acknowledged)
            {
                if (pendingOperation.client_id > 0)
                    await ConduitConnection.TrySendCommandStartedAsync(pendingOperation.client_id, pendingOperation.request_id, pendingOperation.command_type);

                return;
            }

            if (pendingOperation.client_id <= 0)
                return;

            if (!await ConduitConnection.TrySendCommandStartedAsync(pendingOperation.client_id, pendingOperation.request_id, pendingOperation.command_type))
            {
                RemoveQueuedOperation(pendingOperation);
                return;
            }

            lock (stateGate)
            {
                if (ReferenceEquals(activeOperation, pendingOperation) || queuedOperations.Contains(pendingOperation))
                    pendingOperation.is_acknowledged = true;
            }

            PumpQueuedCommands();
        }

        static async Task ExecuteAcceptedCommandAsync(PendingOperationState pendingOperation, ParsedBridgeCommand incomingCommand)
        {
            try
            {
                StartLogCapture();
                ClearCompilerMessages();

                switch (incomingCommand.Kind)
                {
                    case ParsedBridgeCommandKind.Play:
                        StartPlayToggle(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.Screenshot:
                        await ExecuteScreenshotAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.GetDependencies:
                        await ExecuteGetDependenciesAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.FindReferencesTo:
                        await ExecuteFindReferencesToAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.FindMissingScripts:
                        await ExecuteFindMissingScriptsAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.Show:
                        await ExecuteShowAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.Search:
                        await ExecuteSearchAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.ToJson:
                        await ExecuteToJsonAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.FromJsonOverwrite:
                        await ExecuteFromJsonOverwriteAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.SaveScenes:
                        await ExecuteSaveScenesAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.DiscardScenes:
                        await ExecuteDiscardScenesAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.RefreshAssetDatabase:
                        StartReimport();
                        break;
                    case ParsedBridgeCommandKind.ExecuteCode:
                        await ExecuteCodeAsync(pendingOperation);
                        break;
                    case ParsedBridgeCommandKind.RunTestsEditMode:
                        StartTestRun(TestMode.EditMode, false, pendingOperation.test_filter);
                        break;
                    case ParsedBridgeCommandKind.RunTestsPlayMode:
                        StartTestRun(TestMode.PlayMode, false, pendingOperation.test_filter);
                        break;
                    case ParsedBridgeCommandKind.RunTestsPlayer:
                        StartTestRun(TestMode.PlayMode, true, pendingOperation.test_filter);
                        break;
                    default:
                        await CompleteCurrentAsync(
                            new()
                            {
                                outcome = ToolOutcome.Exception,
                                diagnostic = $"Unsupported command '{pendingOperation.command_type}'.",
                            }
                        );

                        break;
                }
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error($"Unhandled exception while executing '{pendingOperation.command_type}'.", exception);
                await CompleteUnhandledCommandExceptionAsync(pendingOperation, exception);
            }
        }

        static void RemoveQueuedOperation(PendingOperationState pendingOperation)
        {
            lock (stateGate)
            {
                var queuedOperationIndex = queuedOperations.IndexOf(pendingOperation);
                if (queuedOperationIndex >= 0)
                    queuedOperations.RemoveAt(queuedOperationIndex);
            }

            PumpQueuedCommands();
        }

        static async Task CompleteUnhandledCommandExceptionAsync(PendingOperationState pendingOperation, Exception exception)
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
                ConduitDiagnostics.Error($"Failed to report unhandled exception for '{pendingOperation.command_type}'.", completionException);
                AbandonActiveOperation(pendingOperation);
                RemoveReimportHooks();
            }
        }

        static BridgeExceptionInfo SafeToExceptionInfo(Exception exception)
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

        internal static void HandleClientDisconnected(int clientId)
        {
            var disconnectedActiveOperation = false;
            PendingOperationState? activeOperation;
            lock (stateGate)
            {
                activeOperation = ConduitToolRunner.activeOperation;
                if (activeOperation?.client_id == clientId)
                {
                    activeOperation.client_id = 0;
                    disconnectedActiveOperation = true;
                }

                foreach (var queuedOperation in queuedOperations)
                    if (queuedOperation.client_id == clientId)
                        queuedOperation.client_id = 0;
            }

            if (disconnectedActiveOperation && activeOperation != null)
                ConduitDiagnostics.Warn($"MCP client disconnected while '{activeOperation.command_type}' was still active. Waiting for the same request id to reconnect.");
        }

        static bool TryFindQueuedOperation(string requestId, string commandType, out PendingOperationState? operation)
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

        static void AbandonActiveOperation(PendingOperationState pendingOperation)
        {
            lock (stateGate)
            {
                if (!ReferenceEquals(activeOperation, pendingOperation))
                    return;

                activeOperation = null;
                activeCommand = default;
            }

            RemovePlayModeHooks();
            ClearPersistedActiveOperation();
            PumpQueuedCommands();
        }

        internal static ParsedBridgeCommand ParseIncomingCommand(string commandType)
            => commandType switch
            {
                BridgeCommandTypes.Status               => new() { Kind = ParsedBridgeCommandKind.Status },
                BridgeCommandTypes.Play                 => new() { Kind = ParsedBridgeCommandKind.Play },
                BridgeCommandTypes.Screenshot           => new() { Kind = ParsedBridgeCommandKind.Screenshot },
                BridgeCommandTypes.GetDependencies      => new() { Kind = ParsedBridgeCommandKind.GetDependencies },
                BridgeCommandTypes.FindReferencesTo     => new() { Kind = ParsedBridgeCommandKind.FindReferencesTo },
                BridgeCommandTypes.FindMissingScripts   => new() { Kind = ParsedBridgeCommandKind.FindMissingScripts },
                BridgeCommandTypes.Show                 => new() { Kind = ParsedBridgeCommandKind.Show },
                BridgeCommandTypes.Search               => new() { Kind = ParsedBridgeCommandKind.Search },
                BridgeCommandTypes.ToJson               => new() { Kind = ParsedBridgeCommandKind.ToJson },
                BridgeCommandTypes.FromJsonOverwrite    => new() { Kind = ParsedBridgeCommandKind.FromJsonOverwrite },
                BridgeCommandTypes.SaveScenes           => new() { Kind = ParsedBridgeCommandKind.SaveScenes },
                BridgeCommandTypes.DiscardScenes        => new() { Kind = ParsedBridgeCommandKind.DiscardScenes },
                BridgeCommandTypes.RefreshAssetDatabase => new() { Kind = ParsedBridgeCommandKind.RefreshAssetDatabase },
                BridgeCommandTypes.ExecuteCode          => new() { Kind = ParsedBridgeCommandKind.ExecuteCode },
                BridgeCommandTypes.RunTestsEditMode     => new() { Kind = ParsedBridgeCommandKind.RunTestsEditMode },
                BridgeCommandTypes.RunTestsPlayMode     => new() { Kind = ParsedBridgeCommandKind.RunTestsPlayMode },
                BridgeCommandTypes.RunTestsPlayer       => new() { Kind = ParsedBridgeCommandKind.RunTestsPlayer },
                _                                       => new() { Kind = ParsedBridgeCommandKind.Unknown },
            };

    }
}
