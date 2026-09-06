using System;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    ProjectCommandQueue GetOrCreateEditorQueue(string projectPath)
    {
        if (queues.TryGetValue(projectPath, out var queue))
            return queue;

        lock (queueCreationGate)
        {
            if (queues.TryGetValue(projectPath, out queue))
                return queue;

            queue = new(
                loggerFactory.CreateLogger<ProjectCommandQueue>(),
                ExecuteQueuedCommandAsync,
                applicationLifetime.ApplicationStopping
            );
            queues[projectPath] = queue;
            return queue;
        }
    }

    async Task<ToolExecutionResult> EnqueueAsync(string projectPath, BridgeCommand command, CT ct)
    {
        command.TrackUsage = true;
        command.IncludeBackgroundLogs = true;
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        if (PlayerSelector.TryParse(normalizedProjectPath, out _))
        {
            if (BridgeCommandKinds.IsProfiler(
                    BridgeCommandKinds.Parse(command.CommandType)
                ))
                return await ExecutePlayerProfilerAsync(
                    normalizedProjectPath,
                    command,
                    ct
                );

            return await EnqueuePlayerAsync(normalizedProjectPath, command, ct);
        }

        var session = projectRegistry.GetOrAddProject(normalizedProjectPath);
        var blockedResult = await TryPrepareProjectAsync(normalizedProjectPath, session, command.CommandType, ct);
        if (blockedResult is { } preparationResult)
            return preparationResult;

        var queue = GetOrCreateEditorQueue(session.ProjectPath);

        var commandTimeout = UnityToolTimeouts.ForCommand(BridgeCommandKinds.Parse(command.CommandType));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(commandTimeout);

        var result = await queue.EnqueueAsync(new(session, command, timeoutCts.Token), timeoutCts.Token);
        if (!ct.IsCancellationRequested
            && timeoutCts.IsCancellationRequested
            && result.Outcome == ToolOutcome.Cancelled)
            return ToolExecutionResult.Timeout(
                commandTimeout,
                $"Unity did not start or finish '{command.CommandType}' within {commandTimeout}."
            );

        return result;
    }

    /*
     * Commands can trust the cached bridge only while it is live. Otherwise fall back
     * to the normal preflight path so failure diagnostics stay accurate.
     */
    async Task<ToolExecutionResult?> TryPrepareProjectAsync(string normalizedProjectPath, ProjectSession session, string commandType, CT ct)
    {
        if (TrySkipOfflinePreflight(session, normalizedProjectPath, out var cachedHandshake))
        {
            if (cachedHandshake is not null)
                await UpdateProjectRegistryAsync(normalizedProjectPath, cachedHandshake, ct);

            return null;
        }

        var preflight = await UnityProjectOfflinePreflight.ExecuteAsync(
            normalizedProjectPath,
            environmentInspector,
            projectRegistry,
            bridgeClient,
            UnityToolTimeouts.StatusWithoutKnownProcess,
            ct
        );

        if (preflight.IsBlocked)
            return ToolExecutionResult.NotConnected(
                normalizedProjectPath,
                FormatBlockedDiagnosticForCommand(commandType, preflight.Diagnostic)
            );

        await UpdateProjectRegistryAsync(normalizedProjectPath, preflight.ProbeExecution?.Handshake, ct);
        return null;
    }

    async Task<ToolExecutionResult> ExecuteQueuedCommandAsync(QueuedProjectCommand queuedCommand, CT ct)
    {
        var context = queuedCommand.Session.StartCommand(queuedCommand.Command);
        var commandKind = BridgeCommandKinds.Parse(queuedCommand.Command.CommandType);
        var commandTimeout = UnityToolTimeouts.ForCommand(commandKind);
        // only commands with an editor-side cancellation contract receive request cancellation.
        var commandCancellation = BridgeCommandKinds.SupportsCancellation(commandKind)
            ? queuedCommand.RequestCancellation
            : default;
        var reachable = false;
        int? monitoredProcessId = null;

        try
        {
            if (BridgeCommandKinds.IsAssetImport(commandKind))
            {
                monitoredProcessId = environmentInspector
                    .Inspect(queuedCommand.Session.ProjectPath)
                    .MatchedProcess
                    ?.ProcessId;

                var recovery = await refreshAssetDatabaseRecoveryCoordinator.ExecuteAsync(
                    queuedCommand.Session.ProjectPath,
                    context.RequestId,
                    queuedCommand.Command,
                    monitoredProcessId,
                    UnityToolTimeouts.RefreshAssetDatabaseActivation,
                    UnityToolTimeouts.StatusCommand,
                    commandTimeout,
                    UnityToolTimeouts.RefreshAssetDatabaseRecoveryPollInterval,
                    ct
                );

                monitoredProcessId = recovery.MonitoredProcessId;
                reachable = recovery.Reachable;
                return recovery.Result;
            }

            var execution = await ExecuteReplayableCommandAsync(commandTimeout);
            var result = execution.Result
                         ?? ToToolExecutionResult(
                             queuedCommand.Session.ProjectPath,
                             queuedCommand.Command.CommandType,
                             execution,
                             commandTimeout,
                             environmentInspector
                         );
            if (commandKind == BridgeCommandKind.RunTestsPlayer)
                await ShutdownTestPlayersAsync(queuedCommand.Session.ProjectPath, result);
            return result;
        }
        finally
        {
            queuedCommand.Session.FinishCommand(context.RequestId, reachable);
        }

        async Task<BridgeClientResult> ExecuteReplayableCommandAsync(TimeSpan timeout)
        {
            var execution = await bridgeClient.ExecuteCommandAsync(
                queuedCommand.Session.ProjectPath,
                context.RequestId,
                queuedCommand.Command,
                timeout,
                monitoredProcessId,
                ct,
                commandCancellation
            );

            await ApplyHandshakeAsync(execution);
            if (!ShouldReplayRequest(execution))
                return execution;

            // recover the common case where unity accepted the command but is blocked in a native reload prompt.
            await sceneReloadPromptRecovery.TryDismissAsync(
                queuedCommand.Session.ProjectPath,
                monitoredProcessId ?? execution.Handshake?.EditorProcessId,
                ct
            );

            var retriedExecution = await bridgeClient.ExecuteCommandAsync(
                queuedCommand.Session.ProjectPath,
                context.RequestId,
                queuedCommand.Command,
                timeout,
                monitoredProcessId,
                ct,
                commandCancellation
            );

            await ApplyHandshakeAsync(retriedExecution);
            return SelectReplayResult(execution, retriedExecution);
        }

        async Task ApplyHandshakeAsync(BridgeClientResult execution)
        {
            if (execution.Handshake is not { } handshake)
            {
                reachable = false;
                return;
            }

            reachable = execution.FailureKind != BridgeRuntimeFailureKind.ProcessExited;
            monitoredProcessId = handshake.EditorProcessId > 0 ? handshake.EditorProcessId : monitoredProcessId;
            await projectRegistry.UpdateFromHandshakeAsync(handshake, ct);
        }
    }

    internal static ToolExecutionResult ToToolExecutionResult(
        string projectPath,
        string commandType,
        BridgeClientResult execution,
        TimeSpan timeout
    )
    {
        if (execution.Result is { } result)
            return result;

        if (execution.FailureKind is null)
            return ToolExecutionResult.NotConnected(projectPath);

        var diagnostic = string.IsNullOrWhiteSpace(execution.FailureDiagnostic)
            ? $"Unity did not complete '{commandType}'."
            : execution.FailureDiagnostic;

        return execution.FailureKind is BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultTimedOut
            ? ToolExecutionResult.Timeout(timeout, diagnostic)
            : ToolExecutionResult.NotConnected(projectPath, diagnostic);
    }

    internal static ToolExecutionResult ToToolExecutionResult(
        string projectPath,
        string commandType,
        BridgeClientResult execution,
        TimeSpan timeout,
        UnityProjectEnvironmentInspector environmentInspector,
        UnityProjectEnvironmentSnapshot? snapshot = null
    )
    {
        var fallback = ToToolExecutionResult(projectPath, commandType, execution, timeout);
        if (execution.Result is not null || execution.Handshake is not null || execution.FailureKind is null)
            return fallback;

        var diagnostic = UnityProjectOfflinePreflight.ResolveBlockedDiagnostic(
            environmentInspector,
            projectPath,
            execution,
            snapshot
        );

        if (string.IsNullOrWhiteSpace(diagnostic))
            return fallback;

        diagnostic = FormatBlockedDiagnosticForCommand(commandType, diagnostic);
        if (diagnostic == fallback.Diagnostic)
            return fallback;

        return fallback.Outcome == ToolOutcome.Timeout
            ? ToolExecutionResult.Timeout(timeout, diagnostic)
            : ToolExecutionResult.NotConnected(projectPath, diagnostic);
    }

    internal static string FormatBlockedDiagnosticForCommand(string commandType, string diagnostic) =>
        commandType == BridgeCommandTypes.RefreshAssetDatabase
        && diagnostic == UnityProjectEnvironmentProbe.SafeModeDiagnostic
            ? UnityProjectEnvironmentProbe.RefreshAssetDatabaseSafeModeDiagnostic
            : diagnostic;

    internal static bool ShouldReplayRequest(BridgeClientResult execution) =>
        execution.Handshake is not null
        && execution.FailureKind is BridgeRuntimeFailureKind.SendFailed
            or BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckDisconnected
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultDisconnected
            or BridgeRuntimeFailureKind.ResultTimedOut;

    internal static BridgeClientResult SelectReplayResult(
        BridgeClientResult execution,
        BridgeClientResult retriedExecution
    ) => retriedExecution is { Handshake: null, FailureKind: BridgeRuntimeFailureKind.ConnectTimedOut }
        ? execution
        : retriedExecution;
}
