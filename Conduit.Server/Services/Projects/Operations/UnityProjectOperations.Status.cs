using System;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    public async Task<string> StatusAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        if (PlayerSelector.TryParse(normalizedProjectPath, out var playerSelector))
            return await StatusPlayerAsync(playerSelector, ct);

        string AppendPlayers(string report) =>
            AppendLivePlayers(report, playerDiscovery.FindForProject(normalizedProjectPath));

        var usage = new StatusUsageState();
        try
        {
            var session = projectRegistry.GetOrAddProject(normalizedProjectPath);
            var optimisticReport = await TryBuildOptimisticStatusReportAsync(
                normalizedProjectPath,
                session,
                usage,
                ct
            );
            if (optimisticReport is { } report)
                return AppendPlayers(report);

            return AppendPlayers(
                await ExecuteStatusWithPreflightAsync(normalizedProjectPath, usage, ct)
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Status failed for project '{ProjectPath}' because an internal timeout or cancellation escaped the normal fallback path.", normalizedProjectPath);
            return AppendPlayers(
                BuildSafeUnexpectedStatusResponse(
                    normalizedProjectPath,
                    "Status probing was cancelled before a response could be formatted."
                )
            );
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Status failed unexpectedly for project '{ProjectPath}'. Falling back to environment diagnostics.", normalizedProjectPath);
            return AppendPlayers(
                BuildSafeUnexpectedStatusResponse(
                    normalizedProjectPath,
                    $"Status probing failed unexpectedly: {exception.Message}"
                )
            );
        }
    }

    /*
     * Healthy editors already have a live bridge connection. Try the cheap Unity-side
     * status call first and only pay for offline diagnostics when that fast path fails
     * to produce a real status payload.
     */
    async Task<string?> TryBuildOptimisticStatusReportAsync(
        string normalizedProjectPath,
        ProjectSession session,
        StatusUsageState usage,
        CT ct
    )
    {
        if (!TrySkipOfflinePreflight(session, normalizedProjectPath, out var cachedHandshake))
            return null;

        var execution = await ExecuteRecoverableStatusCommandAsync(
            normalizedProjectPath,
            cachedHandshake?.EditorProcessId,
            UnityToolTimeouts.StatusCommand,
            usage,
            ct
        );

        if (!TryParsePingSnapshot(execution, out var pingSnapshot))
            return null;

        var report = BuildPingReport(normalizedProjectPath, pingSnapshot);

        await UpdateProjectRegistryAsync(normalizedProjectPath, execution.Handshake, ct);
        return report;
    }

    bool TrySkipOfflinePreflight(
        ProjectSession session,
        string normalizedProjectPath,
        out BridgeProjectHandshake? cachedHandshake)
    {
        if (bridgeClient.TryGetLiveHandshake(normalizedProjectPath, out cachedHandshake))
            return true;

        cachedHandshake = null;
        return session.WasReachableRecently(
            DateTimeOffset.UtcNow,
            recentReachablePreflightBypassWindow
        );
    }

    async Task<string> ExecuteStatusWithPreflightAsync(
        string normalizedProjectPath,
        StatusUsageState usage,
        CT ct
    )
    {
        var preflight = await UnityProjectOfflinePreflight.ExecuteAsync(
            normalizedProjectPath,
            environmentInspector,
            projectRegistry,
            bridgeClient,
            UnityToolTimeouts.StatusWithoutKnownProcess,
            ct
        );

        var timeout = preflight.Snapshot.MatchedProcess is null
            ? UnityToolTimeouts.StatusWithoutKnownProcess
            : UnityToolTimeouts.StatusCommand;

        if (preflight.IsBlocked)
        {
            if (UnityStatusPolicy.ShouldWaitForBlockedStatusProgressWindow(
                    preflight.Snapshot,
                    preflight.Diagnostic,
                    preflight.ProbeExecution
                )
                && await TryWaitForStatusProgressWindowAsync(
                    normalizedProjectPath,
                    preflight.Snapshot,
                    preflight.ProbeExecution,
                    timeout,
                    usage,
                    ct
                )
                is { } progressExecution)
            {
                await UpdateProjectRegistryAsync(normalizedProjectPath, progressExecution.Handshake, ct);
                return BuildStatusResponse(normalizedProjectPath, progressExecution, preflight.Snapshot, timeout);
            }

            return environmentInspector.FormatPingFailure(
                preflight.Snapshot,
                ToolExecutionResult.NotConnected(normalizedProjectPath, preflight.Diagnostic)
            );
        }

        var execution = UnityStatusPolicy.ShouldUseProbeExecutionForStatus(preflight.ProbeExecution)
            ? preflight.ProbeExecution!
            : await ExecuteRecoverableStatusCommandAsync(
                normalizedProjectPath,
                preflight.Snapshot.MatchedProcess?.ProcessId,
                timeout,
                usage,
                ct
            );

        if (await TryWaitForStatusProgressWindowAsync(
                normalizedProjectPath,
                preflight.Snapshot,
                execution,
                timeout,
                usage,
                ct
            )
            is { } recoveredExecution)
            execution = recoveredExecution;

        await UpdateProjectRegistryAsync(normalizedProjectPath, execution.Handshake, ct);
        return BuildStatusResponse(normalizedProjectPath, execution, preflight.Snapshot, timeout);
    }

    async Task<BridgeClientResult> ExecuteRecoverableStatusCommandAsync(
        string normalizedProjectPath,
        int? processIdHint,
        TimeSpan timeout,
        StatusUsageState usage,
        CT ct
    )
    {
        var requestId = BridgeIdentifiers.CreateRequestId();
        var execution = await ExecuteAsync();

        if (!ShouldReplayRequest(execution))
            return execution;

        return await ExecuteAsync();

        async Task<BridgeClientResult> ExecuteAsync()
        {
            var trackUsage = !usage.WasSent;
            var result = await bridgeClient.ExecuteCommandAsync(
                normalizedProjectPath,
                requestId,
                new()
                {
                    CommandType = BridgeCommandTypes.Status,
                    TrackUsage = trackUsage,
                },
                timeout,
                processIdHint,
                ct
            );
            usage.WasSent |= trackUsage && result.CommandSent;
            return result;
        }
    }

    async Task UpdateProjectRegistryAsync(
        string normalizedProjectPath,
        BridgeProjectHandshake? handshake,
        CT ct)
    {
        if (handshake is { } projectHandshake)
            await projectRegistry.UpdateFromHandshakeAsync(projectHandshake, ct);
        else
            projectRegistry.MarkReachable(normalizedProjectPath, false);
    }

    // one MCP status call may poll Unity repeatedly; only its first delivered command is usage.
    sealed class StatusUsageState
    {
        internal bool WasSent;
    }
}
