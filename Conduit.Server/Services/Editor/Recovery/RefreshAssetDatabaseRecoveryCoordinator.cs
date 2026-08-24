using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

sealed class RefreshAssetDatabaseRecoveryCoordinator(
    UnityBridgeClient bridgeClient,
    UnityProjectRegistry projectRegistry,
    UnityProjectEnvironmentInspector environmentInspector,
    UnitySceneReloadPromptRecovery sceneReloadPromptRecovery,
    ILogger<RefreshAssetDatabaseRecoveryCoordinator> logger)
{
    public async Task<RefreshAssetDatabaseRecoveryResult> ExecuteAsync(
        string projectPath,
        string requestId,
        BridgeCommand command,
        int? monitoredProcessId,
        TimeSpan commandTimeout,
        TimeSpan pingTimeout,
        TimeSpan reconnectWindow,
        TimeSpan pollInterval,
        CancellationToken ct
    )
    {
        var initialSnapshot = environmentInspector.Inspect(projectPath);
        var logPath = environmentInspector.ResolveEditorLogPath(initialSnapshot);
        var initialLogSnapshot = environmentInspector.GetEditorLogSnapshot(logPath);
        bool reachable = false;

        var initialExecution = await ExecuteAndTrackAsync(requestId, command, commandTimeout);
        if (initialExecution.Result is { } directResult)
            return CompleteWith(directResult);

        if (initialExecution.FailureKind is BridgeRuntimeFailureKind.ProcessExited)
            return FailWith(command.CommandType, initialExecution, commandTimeout);

        if (!UnityProjectOperations.ShouldReplayRequest(initialExecution))
            return FailWith(command.CommandType, initialExecution, commandTimeout);

        var failureKind = initialExecution.FailureKind is { } failureKindValue
            ? EnumStringCache<BridgeRuntimeFailureKind>.Get(failureKindValue)
            : "<none>";

        logger.ZLogWarning(
            $"'{command.CommandType}' entered recovery for project {projectPath} after {commandTimeout}. "
            + $"FailureKind={failureKind}, Diagnostic={initialExecution.FailureDiagnostic ?? "<none>"}"
        );

        // a native reload prompt keeps the pipe connected but stops editor updates from completing requests.
        if (await sceneReloadPromptRecovery.TryDismissAsync(
                projectPath,
                monitoredProcessId ?? initialExecution.Handshake?.EditorProcessId,
                ct
            ))
        {
            var replayAfterDismiss = await ExecuteAndTrackAsync(requestId, command, commandTimeout);
            if (replayAfterDismiss.Result is { } replayAfterDismissResult)
                return CompleteWith(replayAfterDismissResult);

            if (replayAfterDismiss.FailureKind is BridgeRuntimeFailureKind.ProcessExited)
                return FailWith(command.CommandType, replayAfterDismiss, commandTimeout);

            if (!UnityProjectOperations.ShouldReplayRequest(replayAfterDismiss))
                return FailWith(command.CommandType, replayAfterDismiss, commandTimeout);

            initialExecution = replayAfterDismiss;
        }

        string? lastObservedState = null;
        string? lastStatusIssue = null;
        string? lastLoggedStatusIssue = null;
        var recoveryDeadline = DateTimeOffset.UtcNow + reconnectWindow;

        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < recoveryDeadline)
        {
            if (monitoredProcessId is > 0 && environmentInspector.TryReadProcessRuntime(monitoredProcessId) is null)
                return CompleteWith(
                    BuildProcessExitResult(
                        $"Unity editor process {monitoredProcessId} exited while '{command.CommandType}' was recovering."
                    )
                );

            var probe = await bridgeClient.ProbeAsync(projectPath, monitoredProcessId, ct);
            if (probe.FailureKind is BridgeRuntimeFailureKind.ProcessExited)
                return FailWith(command.CommandType, probe, reconnectWindow);

            if (probe.Handshake is null)
            {
                SetLastStatusIssue(probe.FailureDiagnostic ?? "Bridge probe returned no handshake during refresh recovery.");
                await DelayUntilNextProbeAsync();
                continue;
            }

            await ApplyHandshakeAsync(probe);

            var pingExecution = await ExecuteAndTrackAsync(
                requestIdToUse: BridgeIdentifiers.CreateRequestId(),
                commandToRun: new() { CommandType = BridgeCommandTypes.Status },
                timeout: pingTimeout
            );

            if (pingExecution.FailureKind is BridgeRuntimeFailureKind.ProcessExited)
                return FailWith(BridgeCommandTypes.Status, pingExecution, pingTimeout);

            if (pingExecution.Result?.Outcome != ToolOutcome.Success)
            {
                SetLastStatusIssue(
                    pingExecution.Result is { } pingResult
                        ? $"Status returned outcome '{pingResult.Outcome}' with diagnostic '{pingResult.Diagnostic ?? "<none>"}'."
                        : pingExecution.FailureDiagnostic ?? "Status ping returned no result during refresh recovery."
                );

                await DelayUntilNextProbeAsync();
                continue;
            }

            if (string.IsNullOrWhiteSpace(pingExecution.Result.ReturnValue))
            {
                SetLastStatusIssue("Status returned an empty payload during refresh recovery.");
                await DelayUntilNextProbeAsync();
                continue;
            }

            if (!UnityPingSnapshotParser.TryParse(pingExecution.Result.ReturnValue, out UnityPingSnapshot pingSnapshot))
            {
                SetLastStatusIssue("Status payload could not be parsed during refresh recovery.");
                await DelayUntilNextProbeAsync();
                continue;
            }

            SetLastStatusIssue(null);
            var currentState = DescribeRefreshRecoveryState(pingSnapshot);
            if (lastObservedState != currentState)
            {
                lastObservedState = currentState;
                logger.ZLogInformation($"Refresh recovery state for project {projectPath}: {currentState}");
            }

            if (IsRefreshStillBusy(pingSnapshot))
            {
                await DelayUntilNextProbeAsync();
                continue;
            }

            var replayExecution = await ExecuteAndTrackAsync(requestId, command, commandTimeout);
            if (replayExecution.Result is { } replayResult)
                return CompleteWith(replayResult);

            if (replayExecution.FailureKind is BridgeRuntimeFailureKind.ProcessExited)
                return FailWith(command.CommandType, replayExecution, commandTimeout);

            if (!UnityProjectOperations.ShouldReplayRequest(replayExecution))
                return FailWith(command.CommandType, replayExecution, commandTimeout);

            initialExecution = replayExecution;
            await DelayUntilNextProbeAsync();
        }

        return CompleteWith(
            ToolExecutionResult.Timeout(
                timeout: reconnectWindow,
                diagnostic: BuildTimeoutDiagnostic(initialExecution, command.CommandType, reconnectWindow, lastObservedState, lastStatusIssue)
            )
        );

        async Task<BridgeClientResult> ExecuteAndTrackAsync(string requestIdToUse, BridgeCommand commandToRun, TimeSpan timeout)
        {
            var execution = await bridgeClient.ExecuteCommandAsync(
                projectPath,
                requestIdToUse,
                commandToRun,
                timeout,
                monitoredProcessId,
                ct
            );

            await ApplyHandshakeAsync(execution);
            return execution;
        }

        RefreshAssetDatabaseRecoveryResult CompleteWith(ToolExecutionResult result) =>
            new(result, monitoredProcessId, reachable);

        RefreshAssetDatabaseRecoveryResult FailWith(string commandType, BridgeClientResult execution, TimeSpan timeout)
        {
            var result = UnityProjectOperations.ToToolExecutionResult(
                projectPath,
                commandType,
                execution,
                timeout,
                environmentInspector
            );

            return execution.FailureKind is BridgeRuntimeFailureKind.ProcessExited
                ? CompleteWith(
                    BuildProcessExitResult(result.Diagnostic ?? $"Unity exited before '{commandType}' could complete.")
                )
                : CompleteWith(result);
        }

        ToolExecutionResult BuildProcessExitResult(string diagnostic)
        {
            var compilationDiagnostics = environmentInspector.ReadCompilationDiagnosticsSince(logPath, initialLogSnapshot.Length);
            if (string.IsNullOrWhiteSpace(compilationDiagnostics.ErrorText))
                return ToolExecutionResult.NotConnected(projectPath, diagnostic);

            return ToolExecutionResult.NotConnected(
                projectPath,
                $"{diagnostic}\n\nLatest editor log compilation errors observed after refresh started:\n{compilationDiagnostics.ErrorText}"
            );
        }

        async Task DelayUntilNextProbeAsync()
        {
            var remaining = recoveryDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            var delay = remaining < pollInterval ? remaining : pollInterval;
            await Task.Delay(delay, ct);
        }

        async Task ApplyHandshakeAsync(BridgeClientResult execution)
        {
            if (execution.Handshake is not { } handshake)
                return;

            reachable = execution.FailureKind != BridgeRuntimeFailureKind.ProcessExited;
            monitoredProcessId = handshake.EditorProcessId > 0 ? handshake.EditorProcessId : monitoredProcessId;
            await projectRegistry.UpdateFromHandshakeAsync(handshake, ct);
        }

        void SetLastStatusIssue(string? issue)
        {
            lastStatusIssue = issue;
            if (lastLoggedStatusIssue == issue)
                return;

            lastLoggedStatusIssue = issue;
            if (string.IsNullOrWhiteSpace(issue))
            {
                logger.ZLogInformation($"Refresh recovery status probe for project {projectPath} recovered.");
                return;
            }

            logger.ZLogWarning($"Refresh recovery status issue for project {projectPath}: {issue}");
        }
    }

    internal static bool IsRefreshStillBusy(UnityPingSnapshot pingSnapshot) =>
        pingSnapshot.IsCompiling
        || pingSnapshot.IsUpdating
        || BridgeCommandKinds.IsAssetImport(BridgeCommandKinds.Parse(pingSnapshot.ActiveCommandType));

    internal static string DescribeRefreshRecoveryState(UnityPingSnapshot pingSnapshot) =>
        $"is_compiling={(pingSnapshot.IsCompiling ? "true" : "false")}, "
        + $"is_updating={(pingSnapshot.IsUpdating ? "true" : "false")}, "
        + $"active_command_type='{pingSnapshot.ActiveCommandType ?? "<none>"}'";

    internal static string BuildTimeoutDiagnostic(
        BridgeClientResult initialExecution,
        string commandType,
        TimeSpan reconnectWindow,
        string? lastObservedState,
        string? lastStatusIssue
    )
    {
        var stateSuffix = !string.IsNullOrWhiteSpace(lastObservedState)
            ? $" Last observed status: {lastObservedState}."
            : !string.IsNullOrWhiteSpace(lastStatusIssue)
                ? $" Last status issue: {lastStatusIssue}"
                : string.Empty;

        return $"{initialExecution.FailureDiagnostic} Unity did not reconnect and become idle within {reconnectWindow} after '{commandType}'.{stateSuffix}";
    }
}
