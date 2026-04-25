using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed class UnityProjectOperations(
    UnityProjectRegistry projectRegistry,
    UnityBridgeClient bridgeClient,
    UnityProjectEnvironmentInspector environmentInspector,
    UnityEditorProcessController processController,
    IHostApplicationLifetime applicationLifetime,
    ILoggerFactory loggerFactory)
{
    static readonly TimeSpan recentReachablePreflightBypassWindow = TimeSpan.FromSeconds(10);
    readonly ILogger<UnityProjectOperations> logger = loggerFactory.CreateLogger<UnityProjectOperations>();

    readonly ConcurrentDictionary<string, ProjectCommandQueue> queues
        = new(StringComparer.OrdinalIgnoreCase);

    readonly RefreshAssetDatabaseRecoveryCoordinator refreshAssetDatabaseRecoveryCoordinator
        = new(bridgeClient, projectRegistry, environmentInspector, loggerFactory.CreateLogger<RefreshAssetDatabaseRecoveryCoordinator>());

    public async Task<string> StatusAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        try
        {
            var session = projectRegistry.GetOrAddProject(normalizedProjectPath);
            var optimisticReport = await TryBuildOptimisticStatusReportAsync(normalizedProjectPath, session, ct);
            if (optimisticReport is { } report)
                return report;

            return await ExecuteStatusWithPreflightAsync(normalizedProjectPath, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Status failed for project '{ProjectPath}' because an internal timeout or cancellation escaped the normal fallback path.", normalizedProjectPath);
            return BuildSafeUnexpectedStatusResponse(normalizedProjectPath, "Status probing was cancelled before a response could be formatted.");
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Status failed unexpectedly for project '{ProjectPath}'. Falling back to environment diagnostics.", normalizedProjectPath);
            return BuildSafeUnexpectedStatusResponse(normalizedProjectPath, $"Status probing failed unexpectedly: {exception.Message}");
        }
    }

    public async Task<ToolExecutionResult> RestartAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        return await processController.RestartAsync(normalizedProjectPath, ct);
    }

    public Task<ToolExecutionResult> PlayAsync(string projectPath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Play },
            ct: ct
        );

    public Task<ToolExecutionResult> ScreenshotAsync(string projectPath, string target, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Screenshot, Target = target },
            ct: ct
        );

    public Task<ToolExecutionResult> GetDependenciesAsync(string projectPath, string asset, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.GetDependencies,
                Target = asset,
            },
            ct: ct
        );

    public Task<ToolExecutionResult> FindReferencesToAsync(string projectPath, string asset, bool rebuildCache, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.FindReferencesTo,
                Target = asset,
                RebuildCache = rebuildCache,
            },
            ct: ct
        );

    public Task<ToolExecutionResult> FindMissingScriptsAsync(string projectPath, string assetPattern, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.FindMissingScripts, Target = assetPattern },
            ct: ct
        );

    public Task<ToolExecutionResult> ShowAsync(string projectPath, string asset, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Show, Target = asset },
            ct: ct
        );

    public Task<ToolExecutionResult> SearchAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Search, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> ToJsonAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ToJson, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> FromJsonOverwriteAsync(string projectPath, string query, string json, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.FromJsonOverwrite, Target = query, Snippet = json },
            ct: ct
        );

    public Task<ToolExecutionResult> SaveScenesAsync(string projectPath, string? scenePath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.SaveScenes, Target = scenePath },
            ct: ct
        );

    public Task<ToolExecutionResult> DiscardScenesAsync(string projectPath, string? scenePath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.DiscardScenes, Target = scenePath },
            ct: ct
        );

    public async Task<ToolExecutionResult> RefreshAssetDatabaseAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        return await EnqueueAsync(
            projectPath: normalizedProjectPath,
            command: new() { CommandType = BridgeCommandTypes.RefreshAssetDatabase },
            ct: ct
        );
    }

    public Task<ToolExecutionResult> ExecuteCodeAsync(string projectPath, string snippet, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ExecuteCode, Snippet = snippet },
            ct: ct
        );

    public Task<ToolExecutionResult> ViewBurstAsmAsync(string projectPath, string target, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ViewBurstAsm, Target = target },
            ct: ct
        );

    public Task<ToolExecutionResult> RunTestsEditModeAsync(string projectPath, string? testFilter, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsEditMode,
                TestFilter = testFilter,
            },
            ct
        );

    public Task<ToolExecutionResult> RunTestsPlayModeAsync(string projectPath, string? testFilter, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsPlayMode,
                TestFilter = testFilter,
            },
            ct
        );

    public Task<ToolExecutionResult> RunTestsPlayerAsync(string projectPath, string? testFilter, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsPlayer,
                TestFilter = testFilter,
            },
            ct
        );

    async Task<ToolExecutionResult> EnqueueAsync(string projectPath, BridgeCommand command, CT ct)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var session = projectRegistry.GetOrAddProject(normalizedProjectPath);
        var blockedResult = await TryPrepareProjectAsync(normalizedProjectPath, session, ct);
        if (blockedResult is { } preparationResult)
            return preparationResult;

        var queue = queues.GetOrAdd(
            session.ProjectPath,
            _ => new(
                loggerFactory.CreateLogger<ProjectCommandQueue>(),
                ExecuteQueuedCommandAsync,
                applicationLifetime.ApplicationStopping
            )
        );

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
     * Healthy editors already have a live bridge connection. Try the cheap Unity-side
     * status call first and only pay for offline diagnostics when that fast path fails
     * to produce a real status payload.
     */
    async Task<string?> TryBuildOptimisticStatusReportAsync(string normalizedProjectPath, ProjectSession session, CT ct)
    {
        if (!TrySkipOfflinePreflight(session, normalizedProjectPath, out var cachedHandshake))
            return null;

        var execution = await ExecuteRecoverableStatusCommandAsync(
            normalizedProjectPath,
            cachedHandshake?.EditorProcessId,
            UnityToolTimeouts.StatusCommand,
            ct
        );

        if (!TryBuildPingReport(execution, out var report))
            return null;

        await UpdateProjectRegistryAsync(normalizedProjectPath, execution.Handshake, ct);
        return report;
    }

    async Task<string> ExecuteStatusWithPreflightAsync(string normalizedProjectPath, CT ct)
    {
        var preflight = await UnityProjectOfflinePreflight.ExecuteAsync(
            normalizedProjectPath,
            environmentInspector,
            projectRegistry,
            bridgeClient,
            UnityToolTimeouts.StatusWithoutKnownProcess,
            ct
        );

        if (preflight.IsBlocked)
            return environmentInspector.FormatPingFailure(
                preflight.Snapshot,
                ToolExecutionResult.NotConnected(normalizedProjectPath, preflight.Diagnostic)
            );

        var timeout = preflight.Snapshot.MatchedProcess is null
            ? UnityToolTimeouts.StatusWithoutKnownProcess
            : UnityToolTimeouts.StatusCommand;
        var execution = ShouldUseProbeExecutionForStatus(preflight.ProbeExecution)
            ? preflight.ProbeExecution!
            : await ExecuteRecoverableStatusCommandAsync(normalizedProjectPath, preflight.Snapshot.MatchedProcess?.ProcessId, timeout, ct);

        await UpdateProjectRegistryAsync(normalizedProjectPath, execution.Handshake, ct);
        return BuildStatusResponse(normalizedProjectPath, execution, preflight.Snapshot, timeout);
    }

    /*
     * Commands can trust the cached bridge only while it is live. Otherwise fall back
     * to the normal preflight path so failure diagnostics stay accurate.
     */
    async Task<ToolExecutionResult?> TryPrepareProjectAsync(string normalizedProjectPath, ProjectSession session, CT ct)
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
            return ToolExecutionResult.NotConnected(normalizedProjectPath, preflight.Diagnostic);

        await UpdateProjectRegistryAsync(normalizedProjectPath, preflight.ProbeExecution?.Handshake, ct);
        return null;
    }

    async Task<ToolExecutionResult> ExecuteQueuedCommandAsync(QueuedProjectCommand queuedCommand, CT ct)
    {
        var context = queuedCommand.Session.StartCommand(queuedCommand.Command);
        var commandKind = BridgeCommandKinds.Parse(queuedCommand.Command.CommandType);
        var commandTimeout = UnityToolTimeouts.ForCommand(commandKind);
        var reachable = false;
        int? monitoredProcessId = null;

        try
        {
            if (commandKind == BridgeCommandKind.RefreshAssetDatabase)
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
            await RecoverTimedOutTestCommandAsync(commandKind, queuedCommand.Session.ProjectPath, execution);
            return execution.Result
                   ?? ToToolExecutionResult(
                       queuedCommand.Session.ProjectPath,
                       queuedCommand.Command.CommandType,
                       execution,
                       commandTimeout
                   );
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
                ct
            );

            await ApplyHandshakeAsync(execution);
            if (!ShouldReplayRequest(execution))
                return execution;

            var retriedExecution = await bridgeClient.ExecuteCommandAsync(
                queuedCommand.Session.ProjectPath,
                context.RequestId,
                queuedCommand.Command,
                timeout,
                monitoredProcessId,
                ct
            );

            await ApplyHandshakeAsync(retriedExecution);
            return retriedExecution;
        }

        async Task ApplyHandshakeAsync(BridgeClientResult execution)
        {
            if (execution.Handshake is not { } handshake)
                return;

            reachable = execution.FailureKind != BridgeRuntimeFailureKind.ProcessExited;
            monitoredProcessId = handshake.EditorProcessId > 0 ? handshake.EditorProcessId : monitoredProcessId;
            await projectRegistry.UpdateFromHandshakeAsync(handshake, ct);
        }

        async Task RecoverTimedOutTestCommandAsync(BridgeCommandKind currentCommandKind, string projectPath, BridgeClientResult execution)
        {
            if (!ShouldRecoverTimedOutTestCommand(currentCommandKind, execution))
                return;

            logger.LogWarning(
                "Unity test command '{CommandType}' timed out for project {ProjectPath}. Starting automatic editor recovery.",
                queuedCommand.Command.CommandType,
                projectPath
            );

            using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            recoveryCts.CancelAfter(UnityToolTimeouts.RestartStartupMax);

            try
            {
                var recoveryResult = await RestartAsync(projectPath, recoveryCts.Token);
                logger.LogInformation(
                    "Automatic recovery for timed out Unity test command '{CommandType}' completed with outcome {Outcome}.",
                    queuedCommand.Command.CommandType,
                    recoveryResult.Outcome
                );
            }
            catch (OperationCanceledException) when (recoveryCts.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Automatic recovery for timed out Unity test command '{CommandType}' timed out or was cancelled.",
                    queuedCommand.Command.CommandType
                );
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Automatic recovery for timed out Unity test command '{CommandType}' failed.",
                    queuedCommand.Command.CommandType
                );
            }
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

    public static bool ShouldReplayRequest(BridgeClientResult execution) =>
        execution.Handshake is not null
        && execution.FailureKind is BridgeRuntimeFailureKind.SendFailed
            or BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckDisconnected
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultDisconnected
            or BridgeRuntimeFailureKind.ResultTimedOut;

    internal static bool ShouldRecoverTimedOutTestCommand(BridgeCommandKind currentCommandKind, BridgeClientResult execution) =>
        BridgeCommandKinds.IsTest(currentCommandKind)
        && execution.Handshake is not null
        && (execution.FailureKind is BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultTimedOut);

    public static bool ShouldReportReachableStatus(BridgeClientResult execution) =>
        execution.Handshake is not null
        && execution.FailureKind is not (
            BridgeRuntimeFailureKind.ProcessExited
            or BridgeRuntimeFailureKind.SendFailed
            or BridgeRuntimeFailureKind.StartAckDisconnected
            );

    internal static bool ShouldUseProbeExecutionForStatus(BridgeClientResult? probeExecution)
        => probeExecution?.Result is not null;

    async Task<BridgeClientResult> ExecuteRecoverableStatusCommandAsync(string normalizedProjectPath, int? processIdHint, TimeSpan timeout, CT ct)
    {
        var requestId = ConduitUtility.CreateRequestId();
        var execution = await bridgeClient.ExecuteCommandAsync(
            normalizedProjectPath,
            requestId,
            new() { CommandType = BridgeCommandTypes.Status },
            timeout,
            processIdHint,
            ct
        );

        if (!ShouldReplayRequest(execution))
            return execution;

        return await bridgeClient.ExecuteCommandAsync(
            normalizedProjectPath,
            requestId,
            new() { CommandType = BridgeCommandTypes.Status },
            timeout,
            processIdHint,
            ct
        );
    }

    async Task UpdateProjectRegistryAsync(string normalizedProjectPath, BridgeProjectHandshake? handshake, CT ct)
    {
        if (handshake is { } projectHandshake)
            await projectRegistry.UpdateFromHandshakeAsync(projectHandshake, ct);
        else
            projectRegistry.MarkReachable(normalizedProjectPath, false);
    }

    string BuildStatusResponse(string normalizedProjectPath, BridgeClientResult execution, UnityProjectEnvironmentSnapshot? snapshot = null, TimeSpan? statusTimeout = null)
    {
        if (TryBuildPingReport(execution, out var pingReport))
            return pingReport;

        var currentSnapshot = snapshot ?? environmentInspector.Inspect(normalizedProjectPath);
        var effectiveHandshake = execution.Handshake;
        var processRuntime = environmentInspector.TryReadProcessRuntime(
            environmentInspector.ResolveEditorProcessId(currentSnapshot, effectiveHandshake)
        );
        var compilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(currentSnapshot);

        if (execution.FailureKind is not null)
        {
            if (!ShouldReportReachableStatus(execution))
                return environmentInspector.FormatPingFailure(
                    currentSnapshot,
                    ToToolExecutionResult(normalizedProjectPath, BridgeCommandTypes.Status, execution, statusTimeout ?? UnityToolTimeouts.StatusCommand)
                );

            return environmentInspector.FormatPingReachable(
                currentSnapshot,
                effectiveHandshake!,
                processRuntime,
                compilationDiagnostics,
                execution.FailureDiagnostic ?? string.Empty
            );
        }

        if (execution.Result is { } result)
        {
            if (effectiveHandshake is not null && result.Outcome is (ToolOutcome.Timeout or ToolOutcome.NotConnected))
                return environmentInspector.FormatPingReachable(
                    currentSnapshot,
                    effectiveHandshake,
                    processRuntime,
                    compilationDiagnostics,
                    result.Diagnostic ?? $"Connected to the bridge, but Unity did not complete a status command within {statusTimeout ?? UnityToolTimeouts.StatusCommand}."
                );

            return environmentInspector.FormatPingFailure(currentSnapshot, result);
        }

        return effectiveHandshake is null
            ? environmentInspector.FormatPingFailure(
                currentSnapshot,
                ToolExecutionResult.NotConnected(normalizedProjectPath, "Unity returned an empty status payload before the bridge handshake completed.")
            )
            : environmentInspector.FormatPingReachable(
                currentSnapshot,
                effectiveHandshake,
                processRuntime,
                compilationDiagnostics,
                "Unity returned an empty status payload."
            );
    }

    string BuildUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic)
    {
        var snapshot = environmentInspector.Inspect(normalizedProjectPath);
        if (bridgeClient.TryGetLiveHandshake(normalizedProjectPath, out var liveHandshake) && liveHandshake is not null)
        {
            var processRuntime = environmentInspector.TryReadProcessRuntime(
                environmentInspector.ResolveEditorProcessId(snapshot, liveHandshake)
            );
            var compilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(snapshot);
            return environmentInspector.FormatPingReachable(
                snapshot,
                liveHandshake,
                processRuntime,
                compilationDiagnostics,
                diagnostic
            );
        }

        return environmentInspector.FormatPingFailure(
            snapshot,
            BuildUnexpectedStatusFailureResult(
                normalizedProjectPath,
                snapshot,
                environmentInspector.HasConduitPackageSignal(normalizedProjectPath),
                diagnostic
            )
        );
    }

    internal static ToolExecutionResult BuildUnexpectedStatusFailureResult(
        string normalizedProjectPath,
        UnityProjectEnvironmentSnapshot snapshot,
        bool hasConduitPackageSignal,
        string diagnostic
    )
    {
        var effectiveDiagnostic = snapshot.MatchedProcess is not null && hasConduitPackageSignal
            ? $"{UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic} {diagnostic}"
            : diagnostic;

        return ToolExecutionResult.NotConnected(normalizedProjectPath, effectiveDiagnostic);
    }

    string BuildSafeUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic)
    {
        try
        {
            return BuildUnexpectedStatusResponse(normalizedProjectPath, diagnostic);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Status fallback failed for project '{ProjectPath}'. Returning a minimal diagnostic.", normalizedProjectPath);
            return BuildMinimalUnexpectedStatusResponse(normalizedProjectPath, diagnostic);
        }
    }

    static string BuildMinimalUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic) =>
        $"Project: {normalizedProjectPath}\nBridge: unreachable\nDiagnostic: {diagnostic}";

    bool TrySkipOfflinePreflight(ProjectSession session, string normalizedProjectPath, out BridgeProjectHandshake? cachedHandshake)
    {
        if (bridgeClient.TryGetLiveHandshake(normalizedProjectPath, out cachedHandshake))
            return true;

        cachedHandshake = null;
        return session.WasReachableRecently(DateTimeOffset.UtcNow, recentReachablePreflightBypassWindow);
    }

    static bool TryBuildPingReport(BridgeClientResult execution, out string report)
    {
        if (execution.Result?.Outcome == ToolOutcome.Success
            && !string.IsNullOrWhiteSpace(execution.Result.ReturnValue)
            && UnityPingSnapshotParser.TryParse(execution.Result.ReturnValue, out var pingSnapshot))
        {
            report = UnityProjectStatusFormatter.FormatPingReport(pingSnapshot);
            return true;
        }

        report = string.Empty;
        return false;
    }
}
