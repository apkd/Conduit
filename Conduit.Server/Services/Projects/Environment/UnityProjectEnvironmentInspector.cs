using System.Collections.Concurrent;
using System.Diagnostics;

namespace Conduit;

public sealed class UnityProjectEnvironmentInspector
{
    readonly UnityCompilationDiagnosticsReader compilationDiagnosticsReader = new();
    readonly ConcurrentDictionary<string, EditorLogPathObservation> editorLogPaths
        = new(StringComparer.OrdinalIgnoreCase);

    internal UnityProjectEnvironmentSnapshot Inspect(string projectPath) =>
        UnityProjectEnvironmentProbe.Inspect(projectPath);

    internal string FormatPingFailure(UnityProjectEnvironmentSnapshot snapshot, ToolExecutionResult? bridgeResult)
    {
        var processRuntime = UnityEditorProcessProbe.TryReadProcessRuntime(snapshot.MatchedProcess?.ProcessId);
        var editorLogPath = ResolveEditorLogPath(snapshot);
        var compilationDiagnostics = compilationDiagnosticsReader.ReadLatestCompilationDiagnostics(editorLogPath);
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
        UnityEditorPathResolver.ResolveUnityEditorPath(snapshot, process);

    internal string GetRestartLogPath(string projectPath) =>
        UnityEditorLogProbe.GetRestartLogPath(projectPath);

    internal void RememberEditorLogPath(string projectPath, string? editorLogPath, int? processId)
    {
        if (string.IsNullOrWhiteSpace(editorLogPath))
            return;

        SetEditorLogPath(projectPath, editorLogPath, processId);
    }

    internal string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot)
    {
        var matchedProcess = snapshot.MatchedProcess;
        if (matchedProcess is { } process
            && UnityEditorLogProbe.TryExtractLogFilePathFromCommandLine(process.CommandLine) is not null)
        {
            var configuredPath = UnityEditorLogProbe.ResolveEditorLogPath(snapshot);
            SetEditorLogPath(snapshot.ProjectPath, configuredPath, process.ProcessId);
            return configuredPath;
        }

        // observations belong to the editor process that reported them; once offline,
        // the latest observation remains the best available diagnostic location.
        if (editorLogPaths.TryGetValue(snapshot.ProjectPath, out var observation)
            && (matchedProcess is not { } currentProcess
                || observation.ProcessId == currentProcess.ProcessId))
            return observation.Path;

        var resolvedPath = UnityEditorLogProbe.ResolveEditorLogPath(snapshot);
        if (matchedProcess is { } resolvedProcess)
            SetEditorLogPath(snapshot.ProjectPath, resolvedPath, resolvedProcess.ProcessId);

        return resolvedPath;
    }

    internal bool HasConduitPackageSignal(string projectPath) =>
        UnityProjectPackageProbe.HasConduitPackageSignal(projectPath);

    internal string? TryReadSafeModeDiagnostic(UnityProjectEnvironmentSnapshot snapshot) =>
        UnityEditorProcessProbe.TryReadSafeModeDiagnostic(snapshot);

    internal EditorLogSnapshot GetEditorLogSnapshot(string? logPath) =>
        UnityEditorLogProbe.GetEditorLogSnapshot(logPath);

    internal CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(string? logPath, long startOffset) =>
        compilationDiagnosticsReader.ReadCompilationDiagnosticsSince(logPath, startOffset);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(string? logPath) =>
        compilationDiagnosticsReader.ReadLatestCompilationDiagnostics(logPath);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(UnityProjectEnvironmentSnapshot snapshot) =>
        compilationDiagnosticsReader.ReadLatestCompilationDiagnostics(ResolveEditorLogPath(snapshot));

    internal int? ResolveEditorProcessId(UnityProjectEnvironmentSnapshot snapshot, BridgeProjectHandshake? handshake = null) =>
        UnityEditorProcessProbe.ResolveEditorProcessId(snapshot, handshake);

    internal UnityEditorProcessRuntimeInfo? TryReadProcessRuntime(int? processId) =>
        UnityEditorProcessProbe.TryReadProcessRuntime(processId);

    void SetEditorLogPath(string projectPath, string? editorLogPath, int? processId)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        if (normalizedProjectPath.Length == 0)
            return;

        editorLogPaths[normalizedProjectPath] = new(editorLogPath, processId is > 0 ? processId : null);
    }

    readonly record struct EditorLogPathObservation(string? Path, int? ProcessId);
}
