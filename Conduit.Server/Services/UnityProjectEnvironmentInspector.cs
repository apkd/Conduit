using System.Diagnostics;

namespace Conduit;

public sealed class UnityProjectEnvironmentInspector
{
    readonly UnityProjectEnvironmentProbe probe = new();

    internal UnityProjectEnvironmentSnapshot Inspect(string projectPath) =>
        probe.Inspect(projectPath);

    internal string FormatPingFailure(UnityProjectEnvironmentSnapshot snapshot, ToolExecutionResult? bridgeResult)
    {
        var processRuntime = probe.TryReadProcessRuntime(snapshot.MatchedProcess?.ProcessId);
        var compilationDiagnostics = probe.ReadLatestCompilationDiagnostics(snapshot);
        return UnityProjectStatusFormatter.FormatPingFailure(snapshot, bridgeResult, processRuntime, compilationDiagnostics);
    }

    internal string FormatPingReachable(
        UnityProjectEnvironmentSnapshot snapshot,
        BridgeProjectHandshake handshake,
        UnityEditorProcessRuntimeInfo? processRuntime,
        CompilationDiagnosticSummary compilationDiagnostics,
        string diagnostic
    ) =>
        UnityProjectStatusFormatter.FormatPingReachable(snapshot, handshake, processRuntime, compilationDiagnostics, diagnostic);

    internal string FormatPingReport(UnityPingSnapshot pingSnapshot) =>
        UnityProjectStatusFormatter.FormatPingReport(pingSnapshot);

    internal string? ResolveUnityEditorPath(UnityProjectEnvironmentSnapshot snapshot, Process? process) =>
        probe.ResolveUnityEditorPath(snapshot, process);

    internal string GetRestartLogPath(string projectPath) =>
        probe.GetRestartLogPath(projectPath);

    internal string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot) =>
        probe.ResolveEditorLogPath(snapshot);

    internal bool HasConduitPackageSignal(string projectPath) =>
        probe.HasConduitPackageSignal(projectPath);

    internal string? TryReadSafeModeDiagnostic(UnityProjectEnvironmentSnapshot snapshot) =>
        probe.TryReadSafeModeDiagnostic(snapshot);

    internal EditorLogSnapshot GetEditorLogSnapshot(string? logPath) =>
        probe.GetEditorLogSnapshot(logPath);

    internal CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(string? logPath, long startOffset) =>
        probe.ReadCompilationDiagnosticsSince(logPath, startOffset);

    internal CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(UnityProjectEnvironmentSnapshot snapshot, long startOffset) =>
        probe.ReadCompilationDiagnosticsSince(snapshot, startOffset);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(string? logPath) =>
        probe.ReadLatestCompilationDiagnostics(logPath);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(UnityProjectEnvironmentSnapshot snapshot) =>
        probe.ReadLatestCompilationDiagnostics(snapshot);

    internal string? TryReadCompilationFailureSince(string? logPath, long startOffset) =>
        probe.TryReadCompilationFailureSince(logPath, startOffset);

    internal string? TryReadLatestCompilationFailure(string? logPath) =>
        probe.TryReadLatestCompilationFailure(logPath);

    internal int? ResolveEditorProcessId(UnityProjectEnvironmentSnapshot snapshot, BridgeProjectHandshake? handshake = null) =>
        probe.ResolveEditorProcessId(snapshot, handshake);

    internal UnityEditorProcessRuntimeInfo? TryReadProcessRuntime(int? processId) =>
        probe.TryReadProcessRuntime(processId);
}
