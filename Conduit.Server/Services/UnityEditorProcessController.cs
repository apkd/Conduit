using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
        IReadOnlyDictionary<string, string>? editorEnvironment = null;

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
                    editorEnvironment = TryReadProcessEnvironment(editorProcess.Id);
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
                    editorEnvironment = TryReadProcessEnvironment(editorProcess.Id);
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
            ApplyRestartProcessEnvironment(startInfo, editorEnvironment);
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
            {
                builder.AppendLine(safeModeDiagnostic);
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.NotConnected(projectPath, ConduitUtility.FinishText(builder));
            }

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
        ApplyXdgBaseDirectoryDefaults(startInfo);

        var runtimeDirectoryPath = ResolveRuntimeDirectoryPath();
        var waylandDisplay = ResolveWaylandDisplay(runtimeDirectoryPath);
        var x11Display = ResolveX11Display("/tmp/.X11-unix");

        SetEnvironmentVariableIfMissing(startInfo, "XDG_RUNTIME_DIR", runtimeDirectoryPath);
        SetEnvironmentVariableIfMissing(startInfo, "DISPLAY", x11Display);
        SetEnvironmentVariableIfMissing(startInfo, "WAYLAND_DISPLAY", waylandDisplay);
        SetEnvironmentVariableIfMissing(startInfo, "DBUS_SESSION_BUS_ADDRESS", ResolveSessionBusAddress(runtimeDirectoryPath));
        SetEnvironmentVariableIfMissing(startInfo, "XAUTHORITY", ResolveXAuthorityPath());

        ApplyDesktopSessionDefaults(startInfo, runtimeDirectoryPath, waylandDisplay, x11Display);
        ApplyGtkUserSettingsEnvironment(startInfo);
        ApplyNixOsGraphicalSessionEnvironment(startInfo);
        ApplyUnityLinuxGioMitigations(startInfo);
    }

    internal static void ApplyRestartProcessEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? editorEnvironment
    )
    {
        if (startInfo.UseShellExecute)
            return;

        if (editorEnvironment is { Count: > 0 })
        {
            startInfo.Environment.Clear();
            foreach (var (variableName, value) in editorEnvironment)
                if (IsValidEnvironmentVariableName(variableName))
                    startInfo.Environment[variableName] = value;
        }

        ApplyGraphicalSessionEnvironment(startInfo);
    }

    static bool IsValidEnvironmentVariableName(string variableName) =>
        !string.IsNullOrWhiteSpace(variableName)
        && !variableName.Contains('=', StringComparison.Ordinal)
        && !variableName.Contains('\0', StringComparison.Ordinal);

    internal static IReadOnlyDictionary<string, string>? TryReadProcessEnvironment(int processId)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            return ParseProcessEnvironment(File.ReadAllBytes($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/environ"));
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseProcessEnvironment(byte[] bytes)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            environment[entry[..separatorIndex]] = entry[(separatorIndex + 1)..];
        }

        return environment;
    }

    internal static void ApplyDesktopSessionDefaults(
        ProcessStartInfo startInfo,
        string? runtimeDirectoryPath,
        string? waylandDisplay,
        string? x11Display
    )
    {
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
        {
            SetEnvironmentVariableIfMissing(startInfo, "GDK_BACKEND", "wayland,x11");
            SetEnvironmentVariableIfMissing(startInfo, "NIXOS_OZONE_WL", "1");
            SetEnvironmentVariableIfMissing(startInfo, "QT_QPA_PLATFORM", "wayland;xcb");
            SetEnvironmentVariableIfMissing(startInfo, "XDG_SESSION_TYPE", "wayland");
        }
        else if (!string.IsNullOrWhiteSpace(x11Display))
        {
            SetEnvironmentVariableIfMissing(startInfo, "GDK_BACKEND", "x11");
            SetEnvironmentVariableIfMissing(startInfo, "QT_QPA_PLATFORM", "xcb");
            SetEnvironmentVariableIfMissing(startInfo, "XDG_SESSION_TYPE", "x11");
        }

        SetEnvironmentVariableIfMissing(startInfo, "NO_AT_BRIDGE", "1");

        var currentDesktop = ResolveCurrentDesktop(runtimeDirectoryPath);
        SetEnvironmentVariableIfMissing(startInfo, "XDG_CURRENT_DESKTOP", currentDesktop);
        SetEnvironmentVariableIfMissing(startInfo, "XDG_SESSION_DESKTOP", currentDesktop);
    }

    internal static void ApplyXdgBaseDirectoryDefaults(ProcessStartInfo startInfo)
    {
        var homePath = ResolveHomePath(startInfo);
        if (string.IsNullOrWhiteSpace(homePath))
            return;

        SetEnvironmentVariableIfMissing(startInfo, "XDG_CONFIG_HOME", Path.Combine(homePath, ".config"));
        SetEnvironmentVariableIfMissing(startInfo, "XDG_DATA_HOME", Path.Combine(homePath, ".local", "share"));
        SetEnvironmentVariableIfMissing(startInfo, "XDG_CACHE_HOME", Path.Combine(homePath, ".cache"));
        SetEnvironmentVariableIfMissing(startInfo, "XDG_STATE_HOME", Path.Combine(homePath, ".local", "state"));
        SetEnvironmentVariableIfMissing(startInfo, "XDG_CONFIG_DIRS", "/etc/xdg");
        SetEnvironmentVariableIfMissing(startInfo, "XDG_DATA_DIRS", "/usr/local/share:/usr/share");
    }

    static string? ResolveHomePath(ProcessStartInfo startInfo)
    {
        if (startInfo.Environment.TryGetValue("HOME", out var configuredHomePath)
            && !string.IsNullOrWhiteSpace(configuredHomePath))
            return configuredHomePath;

        var environmentHomePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(environmentHomePath))
        {
            SetEnvironmentVariableIfMissing(startInfo, "HOME", environmentHomePath);
            return environmentHomePath;
        }

        var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profilePath))
            return null;

        SetEnvironmentVariableIfMissing(startInfo, "HOME", profilePath);
        return profilePath;
    }

    internal static void ApplyGtkUserSettingsEnvironment(ProcessStartInfo startInfo)
    {
        var settings = TryReadGtkSettings(startInfo, "gtk-3.0")
                       ?? TryReadGtkSettings(startInfo, "gtk-4.0");
        if (settings is null)
            return;

        SetEnvironmentVariableIfMissing(
            startInfo,
            "GTK_THEME",
            ResolveGtkThemeEnvironmentValue(
                startInfo,
                GetGtkSetting(settings, "gtk-theme-name"),
                GetGtkSetting(settings, "gtk-application-prefer-dark-theme")
            )
        );
        SetEnvironmentVariableIfMissing(startInfo, "XCURSOR_THEME", GetGtkSetting(settings, "gtk-cursor-theme-name"));
        SetEnvironmentVariableIfMissing(startInfo, "XCURSOR_SIZE", GetGtkSetting(settings, "gtk-cursor-theme-size"));
    }

    static Dictionary<string, string>? TryReadGtkSettings(ProcessStartInfo startInfo, string versionDirectoryName)
    {
        if (!startInfo.Environment.TryGetValue("XDG_CONFIG_HOME", out var configHomePath)
            || string.IsNullOrWhiteSpace(configHomePath))
            return null;

        var settingsPath = Path.Combine(configHomePath, versionDirectoryName, "settings.ini");
        try
        {
            if (!File.Exists(settingsPath))
                return null;

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var inSettingsSection = false;
            foreach (var rawLine in File.ReadLines(settingsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is '#' or ';')
                    continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    inSettingsSection = string.Equals(line, "[Settings]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSettingsSection)
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                    settings[key] = value;
            }

            return settings.Count > 0 ? settings : null;
        }
        catch
        {
            return null;
        }
    }

    static string? GetGtkSetting(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    internal static string? ResolveGtkThemeEnvironmentValue(
        ProcessStartInfo startInfo,
        string? themeName,
        string? preferDark
    )
    {
        if (string.IsNullOrWhiteSpace(themeName))
            return null;

        var theme = themeName.Trim();
        if (theme.Contains(':', StringComparison.Ordinal)
            || !IsTruthyGtkSetting(preferDark)
            || GtkThemeDirectoryExists(startInfo, theme))
            return theme;

        const string darkSuffix = "-dark";
        return theme.EndsWith(darkSuffix, StringComparison.OrdinalIgnoreCase)
            ? theme[..^darkSuffix.Length] + ":dark"
            : theme;
    }

    static bool GtkThemeDirectoryExists(ProcessStartInfo startInfo, string themeName)
    {
        try
        {
            foreach (var themeDirectoryPath in EnumerateGtkThemeDirectoryPaths(startInfo, themeName))
                if (Directory.Exists(themeDirectoryPath))
                    return true;
        }
        catch
        {
        }

        return false;
    }

    static IEnumerable<string> EnumerateGtkThemeDirectoryPaths(ProcessStartInfo startInfo, string themeName)
    {
        if (startInfo.Environment.TryGetValue("HOME", out var homePath)
            && !string.IsNullOrWhiteSpace(homePath))
            yield return Path.Combine(homePath, ".themes", themeName);

        if (startInfo.Environment.TryGetValue("XDG_DATA_HOME", out var dataHomePath)
            && !string.IsNullOrWhiteSpace(dataHomePath))
            yield return Path.Combine(dataHomePath, "themes", themeName);

        if (!startInfo.Environment.TryGetValue("XDG_DATA_DIRS", out var dataDirectoryPaths)
            || string.IsNullOrWhiteSpace(dataDirectoryPaths))
            yield break;

        foreach (var dataDirectoryPath in dataDirectoryPaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(dataDirectoryPath, "themes", themeName);
    }

    static bool IsTruthyGtkSetting(string? value) =>
        value?.Trim() is "1" or "true" or "True" or "TRUE" or "yes" or "Yes" or "YES";

    internal static void ApplyNixOsGraphicalSessionEnvironment(ProcessStartInfo startInfo) =>
        ApplyNixOsGraphicalSessionEnvironment(startInfo, "/run/current-system/sw");

    internal static void ApplyNixOsGraphicalSessionEnvironment(ProcessStartInfo startInfo, string systemProfilePath)
    {
        SetEnvironmentVariableIfMissing(startInfo, "NIX_XDG_DESKTOP_PORTAL_DIR", ResolveNixXdgDesktopPortalDirectory(systemProfilePath));
        SetEnvironmentVariableIfMissing(startInfo, "GIO_EXTRA_MODULES", ResolveNixGioExtraModules(systemProfilePath));
    }

    internal static void ApplyUnityLinuxGioMitigations(ProcessStartInfo startInfo)
    {
        SetEnvironmentVariableIfMissing(startInfo, "GIO_USE_VFS", "local");
        SetEnvironmentVariableIfMissing(startInfo, "GTK_USE_PORTAL", "0");
        if (!startInfo.Environment.TryGetValue("DBUS_SESSION_BUS_ADDRESS", out var sessionBusAddress)
            || string.IsNullOrWhiteSpace(sessionBusAddress))
            SetEnvironmentVariableIfMissing(startInfo, "GSETTINGS_BACKEND", "memory");
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

        if (!string.IsNullOrWhiteSpace(currentUserId))
        {
            var runtimeDirectoryPath = Path.Combine(runUserRootPath, currentUserId);
            if (Directory.Exists(runtimeDirectoryPath))
                return runtimeDirectoryPath;
        }

        return ResolveAccessibleRuntimeDirectoryPath(runUserRootPath);
    }

    internal static string? ResolveAccessibleRuntimeDirectoryPath(string runUserRootPath)
    {
        try
        {
            if (!Directory.Exists(runUserRootPath))
                return null;

            foreach (var runtimeDirectoryPath in Directory.EnumerateDirectories(runUserRootPath))
            {
                if (!CanReadDirectory(runtimeDirectoryPath))
                    continue;

                if (Path.Exists(Path.Combine(runtimeDirectoryPath, "bus"))
                    || Directory.EnumerateFileSystemEntries(runtimeDirectoryPath, "wayland-*").Any())
                    return runtimeDirectoryPath;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    static bool CanReadDirectory(string directoryPath)
    {
        try
        {
            Directory.EnumerateFileSystemEntries(directoryPath).FirstOrDefault();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ResolveCurrentUserId()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return GetCurrentUnixUserId().ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
            }
        }

        if (TryReadCurrentUserIdFromProcStatus() is { Length: > 0 } procStatusUserId)
            return procStatusUserId;

        var environmentUserId = Environment.GetEnvironmentVariable("UID");
        return IsUnixUserId(environmentUserId) ? environmentUserId : null;
    }

    [DllImport("libc", EntryPoint = "getuid", SetLastError = false)]
    static extern uint GetCurrentUnixUserId();

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

    internal static string? ResolveCurrentDesktop(string? runtimeDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectoryPath))
            return null;

        if (Directory.Exists(Path.Combine(runtimeDirectoryPath, "hypr")))
            return "Hyprland";

        try
        {
            if (Directory.Exists(runtimeDirectoryPath)
                && Directory.EnumerateFileSystemEntries(runtimeDirectoryPath, "sway-ipc.*.sock").Any())
                return "sway";
        }
        catch
        {
        }

        return null;
    }

    internal static string? ResolveNixXdgDesktopPortalDirectory(string systemProfilePath)
    {
        var portalDirectoryPath = Path.Combine(systemProfilePath, "share", "xdg-desktop-portal", "portals");
        try
        {
            return Directory.Exists(portalDirectoryPath)
                   && Directory.EnumerateFiles(portalDirectoryPath, "*.portal", SearchOption.TopDirectoryOnly).Any()
                ? portalDirectoryPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveNixGioExtraModules(string systemProfilePath)
    {
        var serviceFilePath = Path.Combine(systemProfilePath, "share", "dbus-1", "services", "ca.desrt.dconf.service");
        try
        {
            if (!File.Exists(serviceFilePath))
                return null;

            foreach (var line in File.ReadLines(serviceFilePath))
            {
                if (!line.StartsWith("Exec=", StringComparison.Ordinal))
                    continue;

                var executablePath = line["Exec=".Length..].Trim();
                const string libexecSegment = "/libexec/";
                var libexecIndex = executablePath.IndexOf(libexecSegment, StringComparison.Ordinal);
                if (libexecIndex <= 0)
                    return null;

                var moduleDirectoryPath = Path.Combine(executablePath[..libexecIndex], "lib", "gio", "modules");
                if (Directory.Exists(moduleDirectoryPath))
                    return moduleDirectoryPath;

                return null;
            }
        }
        catch
        {
        }

        return null;
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
        var backupDirectoryPath = Path.Combine(platformProjectPath, "Temp", "__Backupscenes");
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
