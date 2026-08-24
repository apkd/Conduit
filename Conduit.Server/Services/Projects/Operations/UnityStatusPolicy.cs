using System;

namespace Conduit;

static class UnityStatusPolicy
{
    internal static bool ShouldReportReachableStatus(BridgeClientResult execution) =>
        execution.Handshake is not null
        && execution.FailureKind is not (
            BridgeRuntimeFailureKind.ProcessExited
            or BridgeRuntimeFailureKind.SendFailed
            or BridgeRuntimeFailureKind.StartAckDisconnected
            );

    internal static bool ShouldUseProbeExecutionForStatus(BridgeClientResult? probeExecution)
        => probeExecution?.Result is not null;

    internal static bool ShouldWaitForBlockedStatusProgressWindow(
        UnityProjectEnvironmentSnapshot snapshot,
        string diagnostic,
        BridgeClientResult? execution
    ) =>
        !IsTerminalOfflineDiagnostic(diagnostic)
        && ShouldWaitForStatusProgressWindow(snapshot, execution);

    internal static bool ShouldWaitForStatusProgressWindow(UnityProjectEnvironmentSnapshot snapshot, BridgeClientResult? execution)
    {
        if (snapshot.MatchedProcess is null || execution is null)
            return false;

        if (execution.Result?.Outcome == ToolOutcome.Success)
            return false;

        if (execution.FailureKind is null)
            return execution.Result?.Outcome is ToolOutcome.Timeout or ToolOutcome.NotConnected;

        return execution.FailureKind is not BridgeRuntimeFailureKind.ProcessExited
            and not BridgeRuntimeFailureKind.ProtocolMismatch
            and not BridgeRuntimeFailureKind.ProjectMismatch;
    }

    internal static TimeSpan GetStatusProgressTitleChangeWindow(int completedTitleChangeExtensions) =>
        completedTitleChangeExtensions <= 0
            ? UnityToolTimeouts.StatusProgressFirstTitleChangeWindow
            : UnityToolTimeouts.StatusProgressTitleChangeWindow;

    static bool IsTerminalOfflineDiagnostic(string diagnostic) =>
        diagnostic == UnityProjectOfflinePreflight.InvalidProjectDiagnostic
        || diagnostic == UnityProjectOfflinePreflight.MissingPackageDiagnostic
        || diagnostic == UnityProjectOfflinePreflight.OfflineDiagnostic
        || diagnostic == UnityProjectEnvironmentProbe.SafeModeDiagnostic
        || diagnostic == UnityProjectEnvironmentProbe.RefreshAssetDatabaseSafeModeDiagnostic;
}
