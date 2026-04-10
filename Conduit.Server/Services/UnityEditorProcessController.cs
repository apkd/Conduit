using System.Diagnostics;
using System.Text;

namespace Conduit;

public sealed class UnityEditorProcessController(
    UnityBridgeClient bridgeClient,
    UnityProjectEnvironmentInspector environmentInspector)
{
    internal const string RestartedProcessExitedDiagnostic = "The restarted Unity process has shut down or crashed.";

    internal async Task<ToolExecutionResult> RestartAsync(string projectPath, CancellationToken ct)
    {
        var builder = new StringBuilder();
        string? restartLogPath = null;
        Process? editorProcess = null;
        Process? restartedProcess = null;

        try
        {
            var snapshot = environmentInspector.Inspect(projectPath);
            if (!snapshot.IsUnityProject)
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, "The specified path is not a valid Unity project.");

            var dirtySceneResult = await TryCreateDirtySceneBlockAsync(snapshot.ProjectPath, snapshot.MatchedProcess?.ProcessId, ct);
            if (dirtySceneResult is not null)
                return dirtySceneResult;

            builder.AppendLine($"Project: {snapshot.ProjectPath}");

            var probe = await bridgeClient.ProbeAsync(snapshot.ProjectPath, snapshot.MatchedProcess?.ProcessId, ct);
            var handshake = probe.Handshake;
            if (handshake != null)
            {
                editorProcess = ConduitUtility.TryGetProcess(handshake.EditorProcessId);
                if (editorProcess != null)
                {
                    builder.AppendLine($"Found running Unity editor via bridge: pid={editorProcess.Id}");
                    if (await TryTerminateExistingEditorAsync(editorProcess, builder, ct) is { } terminationResult)
                        return terminationResult;
                }
            }
            else if (snapshot.MatchedProcess is { } matchedProcess)
            {
                editorProcess = ConduitUtility.TryGetProcess(matchedProcess.ProcessId);
                if (editorProcess != null)
                {
                    builder.AppendLine($"Found running Unity editor via command line: pid={editorProcess.Id}");
                    if (await TryTerminateExistingEditorAsync(editorProcess, builder, ct) is { } terminationResult)
                        return terminationResult;
                }
            }
            else if (snapshot is { LockfileState: UnityProjectLockfileState.Locked, RunningUnityProcessCount: > 0 })
            {
                builder.AppendLine("Bridge is unreachable while the project lockfile is still held.");
                builder.AppendLine("No exact Unity.exe process could be matched to this project.");
                return ToolExecutionResult.Success(string.Empty, ConduitUtility.FinishText(builder));
            }

            var editorPath = environmentInspector.ResolveUnityEditorPath(snapshot, editorProcess);
            if (string.IsNullOrWhiteSpace(editorPath))
            {
                builder.AppendLine("Could not locate a Unity editor executable for this project.");
                return ToolExecutionResult.Success(string.Empty, ConduitUtility.FinishText(builder));
            }

            var preservedBackupPaths = PreserveSceneBackups(snapshot.ProjectPath);
            if (preservedBackupPaths.Length > 0)
                builder.AppendLine($"Preserved {preservedBackupPaths.Length} scene backup(s) in Assets/_Recovery.");

            restartLogPath = environmentInspector.GetRestartLogPath(snapshot.ProjectPath);
            if (Path.GetDirectoryName(restartLogPath) is { Length: > 0 } logDirectoryPath)
                Directory.CreateDirectory(logDirectoryPath);

            var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(snapshot.ProjectPath);
            restartedProcess = Process.Start(
                new ProcessStartInfo(editorPath)
                {
                    Arguments = BuildLaunchArguments(platformProjectPath, restartLogPath),
                    WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = true,
                }
            );

            if (restartedProcess == null)
            {
                builder.AppendLine("Failed to start the Unity editor process.");
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, ConduitUtility.FinishText(builder));
            }

            builder.AppendLine($"Started Unity editor: {editorPath}");
            return await WaitForRestartedEditorAsync(snapshot.ProjectPath, restartLogPath, restartedProcess, builder, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            builder.AppendLine("Restart canceled.");
            return ToolExecutionResult.Success(string.Empty, ConduitUtility.FinishText(builder));
        }
        catch (Exception exception)
        {
            builder.AppendLine($"Restart encountered an exception: {exception.Message}");
            return ToolExecutionResult.Success(string.Empty, ConduitUtility.FinishText(builder));
        }
        finally
        {
            restartedProcess?.Dispose();
            editorProcess?.Dispose();
        }
    }

    async Task<ToolExecutionResult> WaitForRestartedEditorAsync(
        string projectPath,
        string restartLogPath,
        Process restartedProcess,
        StringBuilder builder,
        CancellationToken ct
    )
    {
        var startupStartedUtc = DateTimeOffset.UtcNow;
        var startupDeadlineUtc = startupStartedUtc + UnityToolTimeouts.RestartStartupMax;
        var currentWindowDeadlineUtc = startupStartedUtc + UnityToolTimeouts.RestartStartupWindow;
        var previousLogSnapshot = environmentInspector.GetEditorLogSnapshot(restartLogPath);

        while (true)
        {
            var pingExecution = await bridgeClient.ExecuteCommandAsync(
                projectPath,
                ConduitUtility.CreateRequestId(),
                new() { CommandType = BridgeCommandTypes.Status },
                UnityToolTimeouts.StatusCommand,
                processIdHint: null,
                ct
            );

            var restartCompilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(restartLogPath);
            if (pingExecution.Result?.Outcome == ToolOutcome.Success)
            {
                builder.AppendLine("Bridge became responsive.");
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.Success(string.Empty, ConduitUtility.FinishText(builder));
            }

            if (HasExited(restartedProcess))
                return ToolExecutionResult.NotConnected(projectPath, RestartedProcessExitedDiagnostic);

            var currentSnapshot = environmentInspector.Inspect(projectPath);
            if (environmentInspector.TryReadSafeModeDiagnostic(currentSnapshot) is { } safeModeDiagnostic)
                return ToolExecutionResult.NotConnected(projectPath, safeModeDiagnostic);

            if (!string.IsNullOrWhiteSpace(restartCompilationDiagnostics.ErrorText))
            {
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.Success(string.Empty, ConduitUtility.FinishText(builder));
            }

            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc >= startupDeadlineUtc)
            {
                builder.AppendLine(
                    $"Startup timed out after {UnityToolTimeouts.RestartStartupMax.TotalMinutes:0} minutes despite continued restart log activity."
                );
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.Timeout(UnityToolTimeouts.RestartStartupMax, ConduitUtility.FinishText(builder));
            }

            if (nowUtc >= currentWindowDeadlineUtc)
            {
                var currentLogSnapshot = environmentInspector.GetEditorLogSnapshot(restartLogPath);
                if (!TryExtendRestartStartupWindow(currentWindowDeadlineUtc, startupDeadlineUtc, previousLogSnapshot, currentLogSnapshot, out var nextWindowDeadlineUtc))
                {
                    builder.AppendLine(
                        $"Startup timed out after {(nowUtc - startupStartedUtc).TotalSeconds:0} seconds because the restart log stopped changing while waiting for a responsive bridge or a compilation failure."
                    );
                    AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                    return ToolExecutionResult.Timeout(nowUtc - startupStartedUtc, ConduitUtility.FinishText(builder));
                }

                previousLogSnapshot = currentLogSnapshot;
                currentWindowDeadlineUtc = nextWindowDeadlineUtc;
                builder.AppendLine(
                    $"Observed restart log activity; extending the startup window to {(currentWindowDeadlineUtc - startupStartedUtc).TotalMinutes:0} minute(s)."
                );
                continue;
            }

            var delay = currentWindowDeadlineUtc - nowUtc;
            if (delay > UnityToolTimeouts.RestartStartupPollInterval)
                delay = UnityToolTimeouts.RestartStartupPollInterval;
            if (delay <= TimeSpan.Zero)
                continue;

            await Task.Delay(delay, ct);
        }
    }

    internal static string BuildLaunchArguments(string platformProjectPath, string logPath) =>
        $"-projectPath \"{platformProjectPath}\" -logFile \"{logPath}\"";

    internal static string[] PreserveSceneBackups(string projectPath)
    {
        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(projectPath);
        var backupDirectoryPath = Path.Combine(platformProjectPath, "Temp", "__BackupScenes");
        if (!Directory.Exists(backupDirectoryPath))
            return Array.Empty<string>();

        var sourceFilePaths = Directory
            .EnumerateFiles(backupDirectoryPath, "*", SearchOption.TopDirectoryOnly)
            .ToArray();

        if (sourceFilePaths.Length == 0)
            return Array.Empty<string>();

        var recoveryDirectoryPath = Path.Combine(platformProjectPath, "Assets", "_Recovery");
        Directory.CreateDirectory(recoveryDirectoryPath);

        var copiedFilePaths = new string[sourceFilePaths.Length];
        for (var index = 0; index < sourceFilePaths.Length; index++)
        {
            var sourceFilePath = sourceFilePaths[index];
            var recoveryFileName = NormalizeRecoveryFileName(Path.GetFileName(sourceFilePath));
            copiedFilePaths[index] = GetUniqueRecoveryPath(recoveryDirectoryPath, recoveryFileName, copiedFilePaths, index);
        }

        for (var index = 0; index < sourceFilePaths.Length; index++)
            File.Copy(sourceFilePaths[index], copiedFilePaths[index], overwrite: false);

        foreach (var sourceFilePath in sourceFilePaths)
            File.Delete(sourceFilePath);

        if (!Directory.EnumerateFileSystemEntries(backupDirectoryPath).Any())
            Directory.Delete(backupDirectoryPath);

        return copiedFilePaths;
    }

    static void AppendLatestCompilationDiagnostics(StringBuilder builder, CompilationDiagnosticSummary restartCompilationDiagnostics)
    {
        if (!restartCompilationDiagnostics.HasAnyDiagnostics)
            return;

        var footer = restartCompilationDiagnostics.ErrorText ?? restartCompilationDiagnostics.WarningText;
        if (string.IsNullOrWhiteSpace(footer))
            return;

        builder.AppendLine();
        builder.AppendLine(footer);
    }

    internal static bool TryExtendRestartStartupWindow(
        DateTimeOffset currentWindowDeadlineUtc,
        DateTimeOffset startupDeadlineUtc,
        EditorLogSnapshot previousLogSnapshot,
        EditorLogSnapshot currentLogSnapshot,
        out DateTimeOffset nextWindowDeadlineUtc
    )
    {
        nextWindowDeadlineUtc = currentWindowDeadlineUtc;
        if (!currentLogSnapshot.HasActivitySince(previousLogSnapshot))
            return false;

        var remaining = startupDeadlineUtc - currentWindowDeadlineUtc;
        if (remaining <= TimeSpan.Zero)
            return false;

        nextWindowDeadlineUtc = currentWindowDeadlineUtc
            + (remaining < UnityToolTimeouts.RestartStartupWindow ? remaining : UnityToolTimeouts.RestartStartupWindow);
        return true;
    }

    async Task<ToolExecutionResult?> TryCreateDirtySceneBlockAsync(string projectPath, int? processIdHint, CancellationToken ct)
    {
        var pingExecution = await bridgeClient.ExecuteCommandAsync(
            projectPath,
            ConduitUtility.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            UnityToolTimeouts.StatusCommand,
            processIdHint,
            ct
        );

        if (pingExecution.Result?.Outcome != ToolOutcome.Success
            || string.IsNullOrWhiteSpace(pingExecution.Result.ReturnValue)
            || !UnityPingSnapshotParser.TryParse(pingExecution.Result.ReturnValue, out var pingSnapshot)
            || pingSnapshot.DirtyScenes.Length == 0)
            return null;

        var builder = new StringBuilder();
        builder.AppendLine("Cannot run 'restart' while scenes have unsaved changes.");
        builder.AppendLine("Dirty scenes:");
        foreach (var dirtyScene in pingSnapshot.DirtyScenes)
            builder.AppendLine("- " + dirtyScene);

        builder.Append("Use '");
        builder.Append(BridgeCommandTypes.SaveScenes);
        builder.Append("' to save them or '");
        builder.Append(BridgeCommandTypes.DiscardScenes);
        builder.Append("' to discard them.");
        return ToolExecutionResult.DirtyScene(ConduitUtility.FinishText(builder));
    }

    async Task<ToolExecutionResult?> TryTerminateExistingEditorAsync(
        Process editorProcess,
        StringBuilder builder,
        CancellationToken ct
    )
    {
        if (await TryCloseGracefullyAsync(editorProcess, ct))
        {
            builder.AppendLine("Graceful shutdown succeeded.");
            return null;
        }

        builder.AppendLine(
            $"Graceful shutdown did not complete within {UnityToolTimeouts.RestartShutdownGracePeriod.TotalSeconds:0} seconds; force killing the editor process tree."
        );
        if (await TryForceKillAsync(editorProcess, ct))
        {
            builder.AppendLine("Force kill succeeded.");
            return null;
        }

        builder.AppendLine(
            $"Force kill did not terminate the editor process tree within {UnityToolTimeouts.RestartShutdownKillWait.TotalSeconds:0} seconds."
        );
        return ToolExecutionResult.Timeout(UnityToolTimeouts.RestartShutdownKillWait, ConduitUtility.FinishText(builder));
    }

    static async Task<bool> TryCloseGracefullyAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited)
            return true;

        if (!process.CloseMainWindow())
            return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UnityToolTimeouts.RestartShutdownGracePeriod);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return process.HasExited;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    static async Task<bool> TryForceKillAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited)
            return true;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return process.HasExited;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UnityToolTimeouts.RestartShutdownKillWait);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }

        return process.HasExited;
    }

    static string NormalizeRecoveryFileName(string fileName)
    {
        if (!fileName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
            return fileName;

        var withoutBackupSuffix = fileName[..^".backup".Length];
        return withoutBackupSuffix.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
            ? withoutBackupSuffix
            : withoutBackupSuffix + ".unity";
    }

    static string GetUniqueRecoveryPath(string directoryPath, string fileName, IReadOnlyList<string> pendingPaths, int pendingCount)
    {
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidatePath = Path.Combine(directoryPath, fileName);
        if (!PathExists(candidatePath, pendingPaths, pendingCount))
            return candidatePath;

        for (var suffix = 2;; suffix++)
        {
            candidatePath = Path.Combine(directoryPath, $"{nameWithoutExtension} ({suffix}){extension}");
            if (!PathExists(candidatePath, pendingPaths, pendingCount))
                return candidatePath;
        }
    }

    static bool PathExists(string candidatePath, IReadOnlyList<string> pendingPaths, int pendingCount)
    {
        if (File.Exists(candidatePath))
            return true;

        for (var index = 0; index < pendingCount; index++)
            if (string.Equals(pendingPaths[index], candidatePath, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    internal static bool HasExited(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }
}
