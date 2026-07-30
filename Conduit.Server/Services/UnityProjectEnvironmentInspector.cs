using System.Collections.Concurrent;
using System.Diagnostics;

namespace Conduit;

public sealed class UnityProjectEnvironmentInspector
{
    readonly UnityProjectEnvironmentProbe probe = new();
    readonly ConcurrentDictionary<string, EditorLogPathObservation> editorLogPaths
        = new(StringComparer.OrdinalIgnoreCase);

    internal UnityProjectEnvironmentSnapshot Inspect(string projectPath) =>
        probe.Inspect(projectPath);

    internal string FormatPingFailure(UnityProjectEnvironmentSnapshot snapshot, ToolExecutionResult? bridgeResult)
    {
        var processRuntime = probe.TryReadProcessRuntime(snapshot.MatchedProcess?.ProcessId);
        var editorLogPath = ResolveEditorLogPath(snapshot);
        var compilationDiagnostics = probe.ReadLatestCompilationDiagnostics(editorLogPath);
        return UnityProjectStatusFormatter.FormatPingFailure(
            snapshot,
            bridgeResult,
            processRuntime,
            compilationDiagnostics,
            editorLogPath
        );
    }

    internal string FormatPingReachable(
        UnityProjectEnvironmentSnapshot snapshot,
        BridgeProjectHandshake handshake,
        UnityEditorProcessRuntimeInfo? processRuntime,
        CompilationDiagnosticSummary compilationDiagnostics,
        string diagnostic
    ) =>
        UnityProjectStatusFormatter.FormatPingReachable(
            snapshot,
            handshake,
            processRuntime,
            compilationDiagnostics,
            ResolveEditorLogPath(snapshot),
            diagnostic
        );

    internal string FormatPingReport(UnityPingSnapshot pingSnapshot, string? fallbackEditorLogPath = null) =>
        UnityProjectStatusFormatter.FormatPingReport(pingSnapshot, fallbackEditorLogPath);

    internal string? ResolveUnityEditorPath(UnityProjectEnvironmentSnapshot snapshot, Process? process) =>
        probe.ResolveUnityEditorPath(snapshot, process);

    internal string GetRestartLogPath(string projectPath) =>
        probe.GetRestartLogPath(projectPath);

    internal void RememberEditorLogPath(string projectPath, string? editorLogPath, int? processId)
    {
        if (string.IsNullOrWhiteSpace(editorLogPath))
            return;

        SetEditorLogPath(projectPath, editorLogPath, processId);
    }

    internal string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot)
    {
        var matchedProcess = snapshot.MatchedProcess;
        if (matchedProcess is not null
            && UnityProjectEnvironmentProbe.TryExtractLogFilePathFromCommandLine(matchedProcess.CommandLine) is not null)
        {
            var configuredPath = probe.ResolveEditorLogPath(snapshot);
            SetEditorLogPath(snapshot.ProjectPath, configuredPath, matchedProcess.ProcessId);
            return configuredPath;
        }

        // observations belong to the editor process that reported them; once offline,
        // the latest observation remains the best available diagnostic location.
        if (editorLogPaths.TryGetValue(snapshot.ProjectPath, out var observation)
            && (matchedProcess is null || observation.ProcessId == matchedProcess.ProcessId))
            return observation.Path;

        var resolvedPath = probe.ResolveEditorLogPath(snapshot);
        if (matchedProcess is not null)
            SetEditorLogPath(snapshot.ProjectPath, resolvedPath, matchedProcess.ProcessId);

        return resolvedPath;
    }

    internal bool HasConduitPackageSignal(string projectPath) =>
        probe.HasConduitPackageSignal(projectPath);

    internal string? TryReadSafeModeDiagnostic(UnityProjectEnvironmentSnapshot snapshot) =>
        probe.TryReadSafeModeDiagnostic(snapshot);

    internal EditorLogSnapshot GetEditorLogSnapshot(string? logPath) =>
        probe.GetEditorLogSnapshot(logPath);

    internal CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(string? logPath, long startOffset) =>
        probe.ReadCompilationDiagnosticsSince(logPath, startOffset);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(string? logPath) =>
        probe.ReadLatestCompilationDiagnostics(logPath);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(UnityProjectEnvironmentSnapshot snapshot) =>
        probe.ReadLatestCompilationDiagnostics(ResolveEditorLogPath(snapshot));

    internal int? ResolveEditorProcessId(UnityProjectEnvironmentSnapshot snapshot, BridgeProjectHandshake? handshake = null) =>
        probe.ResolveEditorProcessId(snapshot, handshake);

    internal UnityEditorProcessRuntimeInfo? TryReadProcessRuntime(int? processId) =>
        probe.TryReadProcessRuntime(processId);

    void SetEditorLogPath(string projectPath, string? editorLogPath, int? processId)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        if (normalizedProjectPath.Length == 0)
            return;

        editorLogPaths[normalizedProjectPath] = new(editorLogPath, processId is > 0 ? processId : null);
    }

    readonly record struct EditorLogPathObservation(string? Path, int? ProcessId);
}
