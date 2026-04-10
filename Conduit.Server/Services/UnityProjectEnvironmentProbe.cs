using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cysharp.Text;

namespace Conduit;

sealed partial class UnityProjectEnvironmentProbe
{
    const string SafeModeDiagnostic = "The unity editor is in safe mode.";

    [GeneratedRegex("-projectPath\\s+(?:\"(?<path>[^\"]+)\"|(?<path>\\S+))", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ProjectPathArgumentRegex();

    [GeneratedRegex("-logFile\\s+(?:\"(?<path>[^\"]*)\"|(?<path>\\S+))", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex LogFileArgumentRegex();

    string LegacyEditorLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Unity",
        "Editor",
        "Editor.log"
    );

    public UnityProjectEnvironmentSnapshot Inspect(string projectPath)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath);
        var projectVersionPath = Path.Combine(platformProjectPath, "ProjectSettings", "ProjectVersion.txt");
        var runningUnityProcesses = QueryUnityProcesses();
        return new(
            normalizedProjectPath,
            File.Exists(projectVersionPath),
            ConduitUtility.TryReadEditorVersion(projectVersionPath),
            InspectLockfile(Path.Combine(platformProjectPath, "Temp", "UnityLockfile")),
            runningUnityProcesses.Count,
            FindMatchingProjectProcess(runningUnityProcesses, normalizedProjectPath)
        );
    }

    public string? ResolveUnityEditorPath(UnityProjectEnvironmentSnapshot snapshot, Process? process)
    {
        var processPath = ConduitUtility.TryGetProcessPath(process) ?? snapshot.MatchedProcess?.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        if (string.IsNullOrWhiteSpace(snapshot.EditorVersion))
            return null;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(programFiles, "Unity", "Hub", "Editor", snapshot.EditorVersion, "Editor", "Unity.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public string GetRestartLogPath(string projectPath)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        return BuildProjectLogPath(normalizedProjectPath);
    }

    public string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot) =>
        ResolveEditorLogPath(snapshot, snapshot.MatchedProcess);

    public string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot, UnityProjectProcessInfo? processInfo) =>
        ResolveEditorLogPath(snapshot.ProjectPath, snapshot.EditorVersion, processInfo?.CommandLine, LegacyEditorLogPath);

    public bool HasConduitPackageSignal(string projectPath)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        if (string.IsNullOrWhiteSpace(normalizedProjectPath))
            return false;

        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath);
        if (File.Exists(Path.Combine(platformProjectPath, "Packages", "dev.tryfinally.conduit", "package.json")))
            return true;

        return ManifestContainsConduitDependency(Path.Combine(platformProjectPath, "Packages", "manifest.json"))
               || LockfileContainsConduitDependency(Path.Combine(platformProjectPath, "Packages", "packages-lock.json"));
    }

    public string? TryReadSafeModeDiagnostic(UnityProjectEnvironmentSnapshot snapshot)
    {
        if (snapshot.MatchedProcess is not { } matchedProcess)
            return null;

        var mainWindowTitle = TryReadMainWindowTitle(matchedProcess.ProcessId) ?? "";
        if (mainWindowTitle is "Enter Safe Mode?" || mainWindowTitle.Contains("SAFE MODE", StringComparison.Ordinal))
            return SafeModeDiagnostic;

        // if (TryReadUiAutomationSafeModeSignal(matchedProcess.ProcessId) is "Enter Safe Mode?" or "SAFE MODE")
        //     return SafeModeDiagnostic;

        return null;
    }

    public EditorLogSnapshot GetEditorLogSnapshot(string? logPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
                return new(length: 0L, lastWriteUtc: null);

            var fileInfo = new FileInfo(logPath);
            DateTimeOffset? lastWriteUtc = fileInfo.LastWriteTimeUtc == default
                ? null
                : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

            return new(fileInfo.Length, lastWriteUtc);
        }
        catch
        {
            return new(length: 0L, lastWriteUtc: null);
        }
    }

    public CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(string? logPath, long startOffset) =>
        ReadCompilationDiagnostics(logPath, startOffset);

    public CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(UnityProjectEnvironmentSnapshot snapshot, long startOffset) =>
        ReadCompilationDiagnostics(ResolveEditorLogPath(snapshot), startOffset);

    public CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(string? logPath) =>
        ReadCompilationDiagnostics(logPath, startOffset: null);

    public CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(UnityProjectEnvironmentSnapshot snapshot) =>
        ReadCompilationDiagnostics(ResolveEditorLogPath(snapshot), startOffset: null);

    public string? TryReadCompilationFailureSince(string? logPath, long startOffset) =>
        ReadCompilationDiagnosticsSince(logPath, startOffset).ErrorText;

    public string? TryReadCompilationFailureSince(UnityProjectEnvironmentSnapshot snapshot, long startOffset) =>
        ReadCompilationDiagnosticsSince(snapshot, startOffset).ErrorText;

    public string? TryReadLatestCompilationFailure(string? logPath) =>
        ReadLatestCompilationDiagnostics(logPath).ErrorText;

    public string? TryReadLatestCompilationFailure(UnityProjectEnvironmentSnapshot snapshot) =>
        ReadLatestCompilationDiagnostics(snapshot).ErrorText;

    public int? ResolveEditorProcessId(UnityProjectEnvironmentSnapshot snapshot, BridgeProjectHandshake? handshake = null)
    {
        if (handshake?.EditorProcessId > 0)
            return handshake.EditorProcessId;

        return snapshot.MatchedProcess?.ProcessId;
    }

    public UnityEditorProcessRuntimeInfo? TryReadProcessRuntime(int? processId)
    {
        if (processId is not > 0)
            return null;

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited)
                return null;

            return new(process.Id, new DateTimeOffset(process.StartTime));
        }
        catch
        {
            return null;
        }
    }

    internal static bool UsesProjectRelativeDefaultEditorLog(string? unityVersion)
    {
        if (string.IsNullOrWhiteSpace(unityVersion))
            return false;

        var version = unityVersion.AsSpan();
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
            return false;

        var remainder = version[(firstDot + 1)..];
        var secondDot = remainder.IndexOf('.');
        var majorSpan = version[..firstDot];
        var minorSpan = secondDot < 0 ? remainder : remainder[..secondDot];
        if (!int.TryParse(majorSpan, out var major))
            return false;

        if (!int.TryParse(minorSpan, out var minor))
            return false;

        return (major, minor) is (> 6000, _) or (6000, >= 5);
    }

    internal static string? ResolveEditorLogPath(
        string normalizedProjectPath,
        string? editorVersion,
        string? commandLine,
        string legacyEditorLogPath
    )
    {
        if (TryExtractLogFilePathFromCommandLine(commandLine) is { } configuredLogPath)
            return ResolveConfiguredLogPath(normalizedProjectPath, configuredLogPath);

        return UsesProjectRelativeDefaultEditorLog(editorVersion)
            ? BuildProjectLogPath(normalizedProjectPath)
            : legacyEditorLogPath;
    }

    internal static string? TryExtractLogFilePathFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var match = LogFileArgumentRegex().Match(commandLine);
        if (!match.Success)
            return null;

        var logPath = match.Groups["path"].Value;
        return string.IsNullOrWhiteSpace(logPath) ? null : logPath;
    }

    static bool ManifestContainsConduitDependency(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return false;

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("dependencies", out var dependencies)
                   && dependencies.ValueKind == JsonValueKind.Object
                   && dependencies.TryGetProperty("dev.tryfinally.conduit", out _);
        }
        catch
        {
            return false;
        }
    }

    static bool LockfileContainsConduitDependency(string lockfilePath)
    {
        if (!File.Exists(lockfilePath))
            return false;

        try
        {
            using var stream = File.OpenRead(lockfilePath);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("dependencies", out var dependencies)
                   && dependencies.ValueKind == JsonValueKind.Object
                   && dependencies.TryGetProperty("dev.tryfinally.conduit", out _);
        }
        catch
        {
            return false;
        }
    }

    static string BuildProjectLogPath(string normalizedProjectPath)
    {
        if (normalizedProjectPath.Length == 0)
            return string.Empty;

        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath);
        return Path.GetFullPath(Path.Combine(platformProjectPath, "Logs", "Editor.log"));
    }

    static string? ResolveConfiguredLogPath(string normalizedProjectPath, string configuredLogPath)
    {
        if (string.IsNullOrWhiteSpace(configuredLogPath)
            || configuredLogPath == "-")
            return null;

        if (Path.IsPathRooted(configuredLogPath))
            return Path.GetFullPath(configuredLogPath);

        if (normalizedProjectPath.Length == 0)
            return null;

        return Path.GetFullPath(configuredLogPath, ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath));
    }

    static string? TryReadMainWindowTitle(int processId)
    {
        using var process = ConduitUtility.TryGetProcess(processId);
        if (process == null)
            return null;

        try
        {
            process.Refresh();
            return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                ? null
                : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadUiAutomationSafeModeSignal(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var powerShellPath = GetPowerShellPath();
        if (!File.Exists(powerShellPath))
            return null;

        try
        {
            using var process = Process.Start(
                new ProcessStartInfo(powerShellPath)
                {
                    Arguments = $"-NoProfile -NonInteractive -EncodedCommand {EncodePowerShellScript(BuildSafeModeUiAutomationScript(processId))}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            );

            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch { }

                return null;
            }

            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                ? output.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    static string BuildSafeModeUiAutomationScript(int processId) =>
        $$"""
          $ErrorActionPreference = 'Stop'
          Add-Type -AssemblyName UIAutomationClient
          Add-Type -AssemblyName UIAutomationTypes
          $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, {{processId}})
          $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Subtree, $condition)
          foreach ($element in $elements) {
              try { $name = $element.Current.Name } catch { $name = $null }
              if ([string]::IsNullOrWhiteSpace($name)) { continue }
              if ($name -match 'Safe Mode|Enter Safe Mode|Exit Safe Mode|compilation errors') {
                  [Console]::Out.Write($name)
                  break
              }
          }
          """;

    static string EncodePowerShellScript(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    static string GetPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe"
    );

    CompilationDiagnosticSummary ReadCompilationDiagnostics(string? logPath, long? startOffset)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
            return CompilationDiagnosticSummary.Empty;

        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (startOffset is > 0 && startOffset.Value < stream.Length)
                stream.Seek(startOffset.Value, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var errors = ZString.CreateStringBuilder();
            var warnings = ZString.CreateStringBuilder();
            var errorCount = 0;
            var warningCount = 0;
            var inBlock = false;
            var sawTundraBlock = false;
            var burstBlockActive = false;
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains("*** Tundra build", StringComparison.Ordinal))
                    {
                        sawTundraBlock = true;
                        ResetCurrentBlock(ref errors, ref warnings, ref inBlock, ref burstBlockActive, ref errorCount, ref warningCount);
                        continue;
                    }

                    if (!sawTundraBlock
                        && (line.Contains("## Script Compilation Error", StringComparison.Ordinal)
                            || line.Contains("## Script Compilation Warning", StringComparison.Ordinal)))
                    {
                        ResetCurrentBlock(ref errors, ref warnings, ref inBlock, ref burstBlockActive, ref errorCount, ref warningCount);
                        continue;
                    }

                    if (!inBlock)
                        continue;

                    if (burstBlockActive)
                    {
                        if (ShouldCaptureBurstContinuation(line))
                        {
                            errors.AppendLine(line);
                            continue;
                        }

                        burstBlockActive = false;
                    }

                    if (line.Contains(": error ", StringComparison.Ordinal))
                    {
                        errors.AppendLine(line);
                        errorCount++;
                        continue;
                    }

                    if (line.Contains(": warning ", StringComparison.Ordinal))
                    {
                        warnings.AppendLine(line);
                        warningCount++;
                        continue;
                    }

                    if (!IsBurstCompilationError(line))
                        continue;

                    errors.AppendLine(line);
                    errorCount++;
                    burstBlockActive = true;
                }

                if (!inBlock)
                    return CompilationDiagnosticSummary.Empty;

                return new(
                    errorCount,
                    warningCount,
                    errors.Length == 0 ? null : ConduitUtility.FinishText(ref errors),
                    warnings.Length == 0 ? null : ConduitUtility.FinishText(ref warnings)
                );
            }
            finally
            {
                warnings.Dispose();
                errors.Dispose();
            }
        }
        catch
        {
            return CompilationDiagnosticSummary.Empty;
        }
    }

    static void ResetCurrentBlock(
        ref Utf16ValueStringBuilder errors,
        ref Utf16ValueStringBuilder warnings,
        ref bool inBlock,
        ref bool burstBlockActive,
        ref int errorCount,
        ref int warningCount
    )
    {
        inBlock = true;
        burstBlockActive = false;
        if (errors.Length > 0)
            errors.Remove(0, errors.Length);

        if (warnings.Length > 0)
            warnings.Remove(0, warnings.Length);

        errorCount = 0;
        warningCount = 0;
    }

    static bool IsBurstCompilationError(string line) =>
        line.Contains(": Burst error BC", StringComparison.Ordinal)
        || line.StartsWith("Burst error BC", StringComparison.Ordinal)
        || line.Contains("InvalidOperationException: Burst failed to compile", StringComparison.Ordinal)
        || line.Contains("BuildFailedException: Burst compiler failed running", StringComparison.Ordinal)
        || line.Contains("Unexpected exception Burst.Compiler.", StringComparison.Ordinal)
        || line.Contains("Burst.Compiler.", StringComparison.Ordinal) && line.Contains("Exception:", StringComparison.Ordinal);

    static bool ShouldCaptureBurstContinuation(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.StartsWith("  at ", StringComparison.Ordinal)
               || line.StartsWith("at Burst.Compiler.", StringComparison.Ordinal)
               || line.StartsWith("Time: -c: line ", StringComparison.Ordinal)
               || line.Contains("linker command line", StringComparison.Ordinal)
               || line.Contains("Burst.Compiler.", StringComparison.Ordinal)
               || line.Contains("This Exception was thrown from a job compiled with Burst", StringComparison.Ordinal)
               || line.StartsWith("(Filename:", StringComparison.Ordinal);
    }

    static UnityProjectLockfileState InspectLockfile(string lockfilePath)
    {
        if (!File.Exists(lockfilePath))
            return UnityProjectLockfileState.Missing;

        try
        {
            using var stream = new FileStream(lockfilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return UnityProjectLockfileState.Stale;
        }
        catch (IOException)
        {
            return UnityProjectLockfileState.Locked;
        }
        catch (UnauthorizedAccessException)
        {
            return UnityProjectLockfileState.Locked;
        }
    }

    static UnityProjectProcessInfo? FindMatchingProjectProcess(
        IReadOnlyList<UnityProjectProcessInfo> runningUnityProcesses,
        string normalizedProjectPath
    )
    {
        foreach (var processInfo in runningUnityProcesses)
        {
            var projectPath = ConduitUtility.TryExtractProjectPathFromCommandLine(processInfo.CommandLine, ProjectPathArgumentRegex());
            if (string.Equals(projectPath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
                return processInfo;
        }

        return null;
    }

    static IReadOnlyList<UnityProjectProcessInfo> QueryUnityProcesses()
    {
        try
        {
            if (ProcessQuery.TryQueryProcessesByName("Unity", out var nativeProcesses))
                return nativeProcesses;

            if (!OperatingSystem.IsWindows())
                return [];

            var powerShellPath = GetPowerShellPath();
            using var process = Process.Start(
                new ProcessStartInfo(powerShellPath)
                {
                    Arguments = "-NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; Get-CimInstance Win32_Process -Filter \\\"name = 'Unity.exe'\\\" | Select-Object ProcessId,ExecutablePath,CommandLine | ConvertTo-Json -Compress\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            );

            if (process == null)
                return [];

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return [];

            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return ToProcessInfo(document.RootElement) is { } processInfo ? [processInfo] : [];

            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var processes = new List<UnityProjectProcessInfo>();
            foreach (var element in document.RootElement.EnumerateArray())
                if (ToProcessInfo(element) is { } processInfo)
                    processes.Add(processInfo);

            return processes.Count == 0 ? [] : processes.ToArray();
        }
        catch
        {
            return [];
        }
    }

    static UnityProjectProcessInfo? ToProcessInfo(JsonElement element)
    {
        if (!element.TryGetProperty("ProcessId", out var processIdElement))
            return null;

        var processId = processIdElement.GetInt32();
        var executablePath = element.TryGetProperty("ExecutablePath", out var executablePathElement)
            ? executablePathElement.GetString()
            : null;

        var commandLine = element.TryGetProperty("CommandLine", out var commandLineElement)
            ? commandLineElement.GetString()
            : null;

        return new(processId, executablePath, commandLine);
    }
}

sealed class CompilationDiagnosticSummary(int errorCount, int warningCount, string? errorText, string? warningText)
{
    public static CompilationDiagnosticSummary Empty { get; } = new(0, 0, null, null);

    public int ErrorCount { get; } = errorCount;

    public int WarningCount { get; } = warningCount;

    public string? ErrorText { get; } = errorText;

    public string? WarningText { get; } = warningText;

    public bool HasAnyDiagnostics => ErrorCount > 0 || WarningCount > 0;
}

sealed class EditorLogSnapshot(long length, DateTimeOffset? lastWriteUtc)
{
    public long Length { get; } = length;

    public DateTimeOffset? LastWriteUtc { get; } = lastWriteUtc;

    public bool HasActivitySince(EditorLogSnapshot previous) =>
        Length != previous.Length || LastWriteUtc != previous.LastWriteUtc;
}

sealed class UnityEditorProcessRuntimeInfo(int processId, DateTimeOffset startedAtUtc)
{
    public int ProcessId { get; } = processId;

    public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
}

sealed class UnityProjectEnvironmentSnapshot(
    string projectPath,
    bool isUnityProject,
    string? editorVersion,
    UnityProjectLockfileState lockfileState,
    int runningUnityProcessCount,
    UnityProjectProcessInfo? matchedProcess)
{
    public string ProjectPath { get; } = projectPath;

    public bool IsUnityProject { get; } = isUnityProject;

    public string? EditorVersion { get; } = editorVersion;

    public UnityProjectLockfileState LockfileState { get; } = lockfileState;

    public int RunningUnityProcessCount { get; } = runningUnityProcessCount;

    public UnityProjectProcessInfo? MatchedProcess { get; } = matchedProcess;
}

sealed class UnityProjectProcessInfo(int processId, string? executablePath, string? commandLine)
{
    public int ProcessId { get; } = processId;

    public string? ExecutablePath { get; } = executablePath;

    public string? CommandLine { get; } = commandLine;
}

enum UnityProjectLockfileState
{
    Missing,
    Locked,
    Stale,
}
