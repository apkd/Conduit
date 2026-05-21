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

            if (!environmentInspector.HasConduitPackageSignal(snapshot.ProjectPath))
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, UnityProjectOfflinePreflight.MissingPackageDiagnostic);

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
            PrepareRestartLogPath(restartLogPath);

            var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(snapshot.ProjectPath);
            var startInfo = CreateLaunchStartInfo(editorPath, platformProjectPath, restartLogPath);
            if (!string.Equals(startInfo.FileName, editorPath, StringComparison.Ordinal))
                builder.AppendLine($"Launching Unity through: {startInfo.FileName}");
            restartedProcess = Process.Start(startInfo);

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
                builder.AppendLine("Unity connection became responsive.");
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

    internal static ProcessStartInfo CreateLaunchStartInfo(string editorPath, string platformProjectPath, string restartLogPath)
        => CreateLaunchStartInfo(
            editorPath,
            platformProjectPath,
            restartLogPath,
            OperatingSystem.IsLinux(),
            File.Exists("/etc/NIXOS"),
            FindExecutableOnPath,
            TryReadAllText
        );

    internal static ProcessStartInfo CreateLaunchStartInfo(
        string editorPath,
        string platformProjectPath,
        string restartLogPath,
        bool isLinux,
        bool isNixOs,
        Func<string, string?> findExecutableOnPath,
        Func<string, string?> readTextFile
    )
    {
        var launchArguments = BuildLaunchArguments(platformProjectPath, restartLogPath);
        if (isLinux)
        {
            var launchExecutablePath = isNixOs && ResolveNixOsUnityWrapper(findExecutableOnPath, readTextFile) is { Length: > 0 } wrapperPath
                ? wrapperPath
                : editorPath;

            var linuxStartInfo = CreateDetachedLinuxStartInfo(
                launchExecutablePath,
                editorPath,
                platformProjectPath,
                restartLogPath,
                findExecutableOnPath
            );
            if (linuxStartInfo is null)
            {
                linuxStartInfo = new(launchExecutablePath)
                {
                    Arguments = string.Equals(launchExecutablePath, editorPath, StringComparison.Ordinal)
                        ? launchArguments
                        : $"{QuoteArgument(editorPath)} {launchArguments}",
                    WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
                    UseShellExecute = false,
                };
            }

            ApplyGraphicalSessionEnvironment(linuxStartInfo);
            return linuxStartInfo;
        }

        var startInfo = new ProcessStartInfo(editorPath)
        {
            Arguments = launchArguments,
            WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
        };

        return startInfo;
    }

    static string? ResolveNixOsUnityWrapper(Func<string, string?> findExecutableOnPath, Func<string, string?> readTextFile)
    {
        if (FindUnityHubFhsEnv(findExecutableOnPath, readTextFile) is { Length: > 0 } unityHubFhsEnvPath)
            return unityHubFhsEnvPath;

        return findExecutableOnPath("steam-run");
    }

    static ProcessStartInfo? CreateDetachedLinuxStartInfo(
        string launchExecutablePath,
        string editorPath,
        string platformProjectPath,
        string restartLogPath,
        Func<string, string?> findExecutableOnPath
    )
    {
        var shellPath = ResolveExecutablePath("bash", findExecutableOnPath, "/bin/bash", "/usr/bin/bash")
                        ?? ResolveExecutablePath("sh", findExecutableOnPath, "/bin/sh", "/usr/bin/sh");
        if (string.IsNullOrWhiteSpace(shellPath))
            return null;

        var startInfo = new ProcessStartInfo(ResolveExecutablePath("setsid", findExecutableOnPath, "/usr/bin/setsid", "/bin/setsid") ?? shellPath)
        {
            WorkingDirectory = Path.GetDirectoryName(editorPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
        };

        if (!string.Equals(startInfo.FileName, shellPath, StringComparison.Ordinal))
            startInfo.ArgumentList.Add(shellPath);

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("\"$@\" & child=$!; wait \"$child\"");
        startInfo.ArgumentList.Add("conduit-unity-launch");
        startInfo.ArgumentList.Add(launchExecutablePath);
        if (!string.Equals(launchExecutablePath, editorPath, StringComparison.Ordinal))
            startInfo.ArgumentList.Add(editorPath);
        startInfo.ArgumentList.Add("-projectPath");
        startInfo.ArgumentList.Add(platformProjectPath);
        startInfo.ArgumentList.Add("-logFile");
        startInfo.ArgumentList.Add(restartLogPath);
        return startInfo;
    }

    static string? ResolveExecutablePath(string executableName, Func<string, string?> findExecutableOnPath, params string[] fallbackPaths)
    {
        if (findExecutableOnPath(executableName) is { Length: > 0 } path)
            return path;

        foreach (var fallbackPath in fallbackPaths)
            if (File.Exists(fallbackPath))
                return fallbackPath;

        return null;
    }

    static string? FindUnityHubFhsEnv(Func<string, string?> findExecutableOnPath, Func<string, string?> readTextFile)
    {
        if (findExecutableOnPath("unityhub-fhs-env") is { Length: > 0 } directPath)
            return directPath;

        var unityHubPath = findExecutableOnPath("unityhub");
        if (string.IsNullOrWhiteSpace(unityHubPath))
            return null;

        return TryExtractUnityHubFhsEnvPath(readTextFile(unityHubPath));
    }

    internal static string? TryExtractUnityHubFhsEnvPath(string? wrapperText)
    {
        if (string.IsNullOrWhiteSpace(wrapperText))
            return null;

        const string marker = "unityhub-fhs-env";
        var searchStart = 0;
        while (true)
        {
            var markerIndex = wrapperText.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (markerIndex < 0)
                return null;

            var start = markerIndex;
            while (start > 0 && !IsShellTokenBoundary(wrapperText[start - 1]))
                start--;

            var end = markerIndex + marker.Length;
            while (end < wrapperText.Length && !IsShellTokenBoundary(wrapperText[end]))
                end++;

            var candidate = wrapperText[start..end];
            if (candidate.EndsWith("/bin/unityhub-fhs-env", StringComparison.Ordinal)
                || string.Equals(Path.GetFileName(candidate), "unityhub-fhs-env", StringComparison.Ordinal))
                return candidate;

            searchStart = markerIndex + marker.Length;
        }
    }

    static bool IsShellTokenBoundary(char character) =>
        char.IsWhiteSpace(character) || character is '"' or '\'';

    static string? FindExecutableOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidatePath = Path.Combine(directory, executableName);
            if (File.Exists(candidatePath))
                return candidatePath;
        }

        return null;
    }

    static string? TryReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch
        {
            return null;
        }
    }

    internal static void ApplyGraphicalSessionEnvironment(ProcessStartInfo startInfo)
    {
        var runtimeDirectoryPath = ResolveRuntimeDirectoryPath();

        SetEnvironmentVariableIfMissing(startInfo, "XDG_RUNTIME_DIR", runtimeDirectoryPath);
        SetEnvironmentVariableIfMissing(startInfo, "DISPLAY", ResolveX11Display("/tmp/.X11-unix"));
        SetEnvironmentVariableIfMissing(startInfo, "WAYLAND_DISPLAY", ResolveWaylandDisplay(runtimeDirectoryPath));
        SetEnvironmentVariableIfMissing(startInfo, "DBUS_SESSION_BUS_ADDRESS", ResolveSessionBusAddress(runtimeDirectoryPath));
        SetEnvironmentVariableIfMissing(startInfo, "XAUTHORITY", ResolveXAuthorityPath());
    }

    static void SetEnvironmentVariableIfMissing(ProcessStartInfo startInfo, string variableName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (startInfo.Environment.TryGetValue(variableName, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
            return;

        startInfo.Environment[variableName] = value;
    }

    static string? ResolveRuntimeDirectoryPath()
        => ResolveRuntimeDirectoryPath(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            ResolveCurrentUserId(),
            "/run/user"
        );

    internal static string? ResolveRuntimeDirectoryPath(string? configuredPath, string? currentUserId, string runUserRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
            return configuredPath;

        if (string.IsNullOrWhiteSpace(currentUserId))
            return null;

        var runtimeDirectoryPath = Path.Combine(runUserRootPath, currentUserId);
        return Directory.Exists(runtimeDirectoryPath) ? runtimeDirectoryPath : null;
    }

    static string? ResolveCurrentUserId()
    {
        if (TryReadCurrentUserIdFromProcStatus() is { Length: > 0 } procStatusUserId)
            return procStatusUserId;

        var environmentUserId = Environment.GetEnvironmentVariable("UID");
        return IsUnixUserId(environmentUserId) ? environmentUserId : null;
    }

    static string? TryReadCurrentUserIdFromProcStatus()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                    continue;

                foreach (var value in line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IsUnixUserId(value))
                        return value;
                }

                return null;
            }
        }
        catch
        {
        }

        return null;
    }

    static bool IsUnixUserId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
                return false;
        }

        return true;
    }

    internal static string? ResolveX11Display(string socketDirectoryPath)
    {
        try
        {
            if (!Directory.Exists(socketDirectoryPath))
                return null;

            int? displayNumber = null;
            foreach (var socketPath in Directory.EnumerateFileSystemEntries(socketDirectoryPath, "X*"))
            {
                var fileName = Path.GetFileName(socketPath);
                if (fileName.Length <= 1 || !int.TryParse(fileName[1..], out var candidateNumber))
                    continue;

                if (displayNumber is null || candidateNumber < displayNumber)
                    displayNumber = candidateNumber;
            }

            return displayNumber is { } resolvedDisplayNumber ? $":{resolvedDisplayNumber}" : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveWaylandDisplay(string? runtimeDirectoryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(runtimeDirectoryPath) || !Directory.Exists(runtimeDirectoryPath))
                return null;

            string? displayName = null;
            foreach (var socketPath in Directory.EnumerateFileSystemEntries(runtimeDirectoryPath, "wayland-*"))
            {
                var fileName = Path.GetFileName(socketPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                if (displayName is null || string.CompareOrdinal(fileName, displayName) < 0)
                    displayName = fileName;
            }

            return displayName;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveSessionBusAddress(string? runtimeDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectoryPath))
            return null;

        var busPath = Path.Combine(runtimeDirectoryPath, "bus");
        return Path.Exists(busPath) ? $"unix:path={busPath}" : null;
    }

    static string? ResolveXAuthorityPath()
    {
        if (Environment.GetEnvironmentVariable("XAUTHORITY") is { Length: > 0 } configuredPath
            && Path.Exists(configuredPath))
            return configuredPath;

        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homePath))
            return null;

        var xAuthorityPath = Path.Combine(homePath, ".Xauthority");
        return Path.Exists(xAuthorityPath) ? xAuthorityPath : null;
    }

    static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    internal static void PrepareRestartLogPath(string restartLogPath)
    {
        if (string.IsNullOrWhiteSpace(restartLogPath))
            return;

        try
        {
            using var stream = new FileStream(
                restartLogPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete
            );
        }
        catch
        {
            // Best-effort only; restart still proceeds and may fall back to stale log content if this fails.
        }
    }

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
