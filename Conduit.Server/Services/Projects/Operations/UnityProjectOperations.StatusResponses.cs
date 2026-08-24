using System;
using Microsoft.Extensions.Logging;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    internal static ToolExecutionResult BuildUnexpectedStatusFailureResult(
        string normalizedProjectPath,
        UnityProjectEnvironmentSnapshot snapshot,
        bool hasConduitPackageSignal,
        string diagnostic
    )
    {
        var effectiveDiagnostic = diagnostic;
        if (snapshot.MatchedProcess is not null && hasConduitPackageSignal)
            effectiveDiagnostic = $"{UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic} {diagnostic}";
        else if (snapshot.IsUnityProject && !hasConduitPackageSignal)
            effectiveDiagnostic = $"{UnityProjectOfflinePreflight.MissingPackageDiagnostic} {diagnostic}";

        return ToolExecutionResult.NotConnected(normalizedProjectPath, effectiveDiagnostic);
    }

    static string BuildMinimalUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic) =>
        $"Project: {normalizedProjectPath}\nBridge: unreachable\nDiagnostic: {diagnostic}";

    static bool TryParsePingSnapshot(BridgeClientResult execution, out UnityPingSnapshot pingSnapshot)
    {
        if (execution.Result?.Outcome == ToolOutcome.Success
            && !string.IsNullOrWhiteSpace(execution.Result.ReturnValue)
            && UnityPingSnapshotParser.TryParse(execution.Result.ReturnValue, out var parsedSnapshot))
        {
            pingSnapshot = parsedSnapshot;
            return true;
        }

        pingSnapshot = new();
        return false;
    }


    string BuildStatusResponse(string normalizedProjectPath, BridgeClientResult execution, UnityProjectEnvironmentSnapshot? snapshot = null, TimeSpan? statusTimeout = null)
    {
        if (TryParsePingSnapshot(execution, out var pingSnapshot))
            return BuildPingReport(normalizedProjectPath, pingSnapshot, snapshot);

        var currentSnapshot = snapshot ?? environmentInspector.Inspect(normalizedProjectPath);
        var effectiveHandshake = execution.Handshake;
        var processRuntime = environmentInspector.TryReadProcessRuntime(
            environmentInspector.ResolveEditorProcessId(currentSnapshot, effectiveHandshake)
        );
        var compilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(currentSnapshot);

        if (execution.FailureKind is not null)
        {
            if (!UnityStatusPolicy.ShouldReportReachableStatus(execution))
                return environmentInspector.FormatPingFailure(
                    currentSnapshot,
                    ToToolExecutionResult(
                        normalizedProjectPath,
                        BridgeCommandTypes.Status,
                        execution,
                        statusTimeout ?? UnityToolTimeouts.StatusCommand,
                        environmentInspector,
                        currentSnapshot
                    )
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


    string BuildPingReport(
        string normalizedProjectPath,
        UnityPingSnapshot pingSnapshot,
        UnityProjectEnvironmentSnapshot? snapshot = null
    )
    {
        environmentInspector.RememberEditorLogPath(
            normalizedProjectPath,
            pingSnapshot.EditorLogPath,
            pingSnapshot.EditorProcessId
        );

        var fallbackEditorLogPath = string.IsNullOrWhiteSpace(pingSnapshot.EditorLogPath)
            ? environmentInspector.ResolveEditorLogPath(
                snapshot ?? environmentInspector.Inspect(normalizedProjectPath)
            )
            : null;

        return environmentInspector.FormatPingReport(pingSnapshot, fallbackEditorLogPath);
    }
}
