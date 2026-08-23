using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cysharp.Text;

namespace Conduit;

sealed partial class UnityProjectEnvironmentProbe
{
    const int CompilationDiagnosticsCachePruneThreshold = 64;
    internal const string SafeModeDiagnostic = "The Unity Editor is in safe mode.";
    internal const string RefreshAssetDatabaseSafeModeDiagnostic =
        "The Unity Editor is in safe mode. (To recompile scripts in safe mode, use the `restart` tool.)";

    [GeneratedRegex("-(?:projectPath|createproject)\\s+(?:\"(?<path>[^\"]+)\"|(?<path>\\S+))", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex EditorProjectPathArgumentRegex();

    [GeneratedRegex("(?:^|\\s)(?:-adb2(?:\\s|$)|-parentPid(?:\\s|$)|-name\\s+\"?AssetImportWorker)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex AuxiliaryUnityProcessArgumentRegex();

    [GeneratedRegex("-logFile\\s+(?:\"(?<path>[^\"]*)\"|(?<path>\\S+))", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex LogFileArgumentRegex();

    readonly ConcurrentDictionary<string, CachedCompilationDiagnostics> compilationDiagnosticsCache =
        new(StringComparer.OrdinalIgnoreCase);

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
        var editorVersion = ConduitUtility.TryReadEditorVersion(projectVersionPath);
        var runningUnityProcesses = QueryUnityProcesses();
        return new(
            normalizedProjectPath,
            editorVersion != null || File.Exists(projectVersionPath),
            editorVersion,
            InspectLockfile(Path.Combine(platformProjectPath, "Temp", "UnityLockfile")),
            runningUnityProcesses.Count,
            FindMatchingProjectProcess(runningUnityProcesses, normalizedProjectPath)
        );
    }

    public string? ResolveUnityEditorPath(UnityProjectEnvironmentSnapshot snapshot, Process? process)
    {
        var processPath = ConduitUtility.TryGetProcessPath(process) ?? snapshot.MatchedProcess?.ExecutablePath;
        return ResolveUnityEditorPath(
            snapshot.EditorVersion,
            processPath,
            Environment.GetEnvironmentVariable("CONDUIT_UNITY_EDITOR"),
            Environment.GetEnvironmentVariable("UNITY_EDITOR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            File.Exists
        );
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

        var mainWindowTitle = UnityWindowTitleProbe.TryReadMainWindowTitle(matchedProcess.ProcessId) ?? "";
        if (SafeModeWindowProbe.IsSafeModeWindowTitle(mainWindowTitle))
            return SafeModeDiagnostic;

        if (SafeModeWindowProbe.TryReadSafeModeWindowSignal(
                matchedProcess.ProcessId,
                mainWindowTitle
            ) is not null)
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

    public CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(string? logPath) =>
        ReadCompilationDiagnostics(logPath, startOffset: null);

    public CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(UnityProjectEnvironmentSnapshot snapshot) =>
        ReadCompilationDiagnostics(ResolveEditorLogPath(snapshot), startOffset: null);

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

    internal static string? ResolveUnityEditorPath(
        string? editorVersion,
        string? processPath,
        string? conduitEditorOverride,
        string? unityEditorOverride,
        string userHome,
        string programFiles,
        bool isWindows,
        bool isLinux,
        Func<string, bool> fileExists
    )
    {
        foreach (var candidate in EnumerateUnityEditorPathCandidates(
                     editorVersion,
                     processPath,
                     conduitEditorOverride,
                     unityEditorOverride,
                     userHome,
                     programFiles,
                     isWindows,
                     isLinux
                 ))
            if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
                return candidate;

        return null;
    }

    static IEnumerable<string> EnumerateUnityEditorPathCandidates(
        string? editorVersion,
        string? processPath,
        string? conduitEditorOverride,
        string? unityEditorOverride,
        string userHome,
        string programFiles,
        bool isWindows,
        bool isLinux
    )
    {
        if (!string.IsNullOrWhiteSpace(processPath))
            yield return processPath;

        if (!string.IsNullOrWhiteSpace(conduitEditorOverride))
            yield return conduitEditorOverride;

        if (!string.IsNullOrWhiteSpace(unityEditorOverride))
            yield return unityEditorOverride;

        if (string.IsNullOrWhiteSpace(editorVersion))
            yield break;

        if (isWindows && !string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity.exe");
            yield return Path.Combine(programFiles, "Unity", "Editor", "Unity.exe");
        }

        if (!isLinux)
            yield break;

        yield return Path.Combine("/opt", "unity", editorVersion, "Editor", "Unity");
        yield return Path.Combine("/opt", "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");

        if (string.IsNullOrWhiteSpace(userHome))
            yield break;

        yield return Path.Combine(userHome, "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
        yield return Path.Combine(userHome, ".local", "share", "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
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
        if (string.IsNullOrWhiteSpace(logPath))
            return CompilationDiagnosticSummary.Empty;
        if (!File.Exists(logPath))
        {
            compilationDiagnosticsCache.TryRemove(logPath, out _);
            return CompilationDiagnosticSummary.Empty;
        }

        try
        {
            long? cacheableLength = null;
            DateTime cacheableLastWriteUtc = default;
            CachedCompilationDiagnostics? exact = null;
            CachedCompilationDiagnostics? resume = null;
            if (startOffset is null)
            {
                var fileInfo = new FileInfo(logPath);
                cacheableLength = fileInfo.Length;
                cacheableLastWriteUtc = fileInfo.LastWriteTimeUtc;
                if (compilationDiagnosticsCache.TryGetValue(logPath, out var cached))
                {
                    if (cached.Length == cacheableLength
                        && cached.LastWriteUtc == cacheableLastWriteUtc)
                        exact = cached;
                    else if (cached.Length < cacheableLength.Value)
                        resume = cached;
                }
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (exact != null)
            {
                if (HasMatchingTail(stream, exact))
                    return exact.Summary;

                stream.Seek(0, SeekOrigin.Begin);
            }
            else if (startOffset is > 0 && startOffset.Value < stream.Length)
                stream.Seek(startOffset.Value, SeekOrigin.Begin);
            else if (resume != null)
            {
                if (CanResumeFrom(stream, resume))
                    stream.Seek(resume.Length, SeekOrigin.Begin);
                else
                {
                    resume = null;
                    stream.Seek(0, SeekOrigin.Begin);
                }
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: stream.Position == 0,
                bufferSize: 1024
            );
            var errors = ZString.CreateStringBuilder();
            var warnings = ZString.CreateStringBuilder();
            var seenErrors = resume == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new(resume.SeenErrors, StringComparer.Ordinal);
            var seenWarnings = resume == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new(resume.SeenWarnings, StringComparer.Ordinal);
            var errorCount = resume?.Summary.ErrorCount ?? 0;
            var warningCount = resume?.Summary.WarningCount ?? 0;
            var inBlock = resume?.InBlock ?? false;
            var sawTundraBlock = resume?.SawTundraBlock ?? false;
            var burstBlockActive = resume?.BurstBlockActive ?? false;
            AppendCachedText(ref errors, resume?.Summary.ErrorText);
            AppendCachedText(ref warnings, resume?.Summary.WarningText);
            try
            {
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains("*** Tundra build", StringComparison.Ordinal))
                    {
                        sawTundraBlock = true;
                        ResetCurrentBlock(
                            ref errors,
                            ref warnings,
                            seenErrors,
                            seenWarnings,
                            ref inBlock,
                            ref burstBlockActive,
                            ref errorCount,
                            ref warningCount
                        );
                        continue;
                    }

                    if (!sawTundraBlock
                        && (line.Contains("## Script Compilation Error", StringComparison.Ordinal)
                            || line.Contains("## Script Compilation Warning", StringComparison.Ordinal)))
                    {
                        ResetCurrentBlock(
                            ref errors,
                            ref warnings,
                            seenErrors,
                            seenWarnings,
                            ref inBlock,
                            ref burstBlockActive,
                            ref errorCount,
                            ref warningCount
                        );
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
                        AppendUniqueDiagnostic(ref errors, seenErrors, line, ref errorCount);
                        continue;
                    }

                    if (line.Contains(": warning ", StringComparison.Ordinal))
                    {
                        AppendUniqueDiagnostic(ref warnings, seenWarnings, line, ref warningCount);
                        continue;
                    }

                    if (!IsBurstCompilationError(line))
                        continue;

                    burstBlockActive = AppendUniqueDiagnostic(ref errors, seenErrors, line, ref errorCount);
                }

                var summary = !inBlock
                    ? CompilationDiagnosticSummary.Empty
                    : new(
                        errorCount,
                        warningCount,
                        errors.Length == 0 ? null : ConduitUtility.FinishText(ref errors),
                        warnings.Length == 0 ? null : ConduitUtility.FinishText(ref warnings)
                    );
                if (cacheableLength is { } length)
                {
                    var fileInfo = new FileInfo(logPath);
                    if (fileInfo.Length == length
                        && fileInfo.LastWriteTimeUtc == cacheableLastWriteUtc)
                    {
                        reader.DiscardBufferedData();
                        var tail = ReadTail(stream, length);
                        fileInfo.Refresh();
                        if (fileInfo.Length == length
                            && fileInfo.LastWriteTimeUtc == cacheableLastWriteUtc)
                            compilationDiagnosticsCache[logPath] = new(
                                length,
                                cacheableLastWriteUtc,
                                summary,
                                inBlock,
                                sawTundraBlock,
                                burstBlockActive,
                                seenErrors,
                                seenWarnings,
                                tail
                            );
                        PruneMissingCompilationDiagnostics();
                    }
                }

                return summary;
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

    void PruneMissingCompilationDiagnostics()
    {
        if (compilationDiagnosticsCache.Count <= CompilationDiagnosticsCachePruneThreshold)
            return;

        foreach (var path in compilationDiagnosticsCache.Keys)
            if (!File.Exists(path))
                compilationDiagnosticsCache.TryRemove(path, out _);
    }

    static void AppendCachedText(ref Utf16ValueStringBuilder builder, string? text)
    {
        if (text is not { Length: > 0 })
            return;

        builder.Append(text);
        builder.Append('\n');
    }

    // a trailing fingerprint proves that a larger log is an append rather than a replacement.
    static bool CanResumeFrom(FileStream stream, CachedCompilationDiagnostics cached)
    {
        if (!cached.CanResume || cached.Length > stream.Length)
            return false;

        if (cached.Tail.Length == 0)
            return cached.Length == 0;

        stream.Seek(cached.Length - cached.Tail.Length, SeekOrigin.Begin);
        Span<byte> tail = stackalloc byte[cached.Tail.Length];
        stream.ReadExactly(tail);
        return tail.SequenceEqual(cached.Tail);
    }

    // metadata timestamps can repeat for same-length rewrites on coarse filesystems.
    static bool HasMatchingTail(FileStream stream, CachedCompilationDiagnostics cached)
    {
        if (cached.Length != stream.Length)
            return false;
        if (cached.Tail.Length == 0)
            return cached.Length == 0;

        stream.Seek(cached.Length - cached.Tail.Length, SeekOrigin.Begin);
        Span<byte> tail = stackalloc byte[cached.Tail.Length];
        stream.ReadExactly(tail);
        return tail.SequenceEqual(cached.Tail);
    }

    static byte[] ReadTail(FileStream stream, long length)
    {
        const int maximumLength = 256;
        var tail = new byte[(int)Math.Min(maximumLength, length)];
        if (tail.Length == 0)
            return tail;

        stream.Seek(length - tail.Length, SeekOrigin.Begin);
        stream.ReadExactly(tail);
        return tail;
    }

    sealed record CachedCompilationDiagnostics(
        long Length,
        DateTime LastWriteUtc,
        CompilationDiagnosticSummary Summary,
        bool InBlock,
        bool SawTundraBlock,
        bool BurstBlockActive,
        HashSet<string> SeenErrors,
        HashSet<string> SeenWarnings,
        byte[] Tail)
    {
        public bool CanResume => Length == 0 || Tail.Length > 0 && Tail[^1] == (byte)'\n';
    }

    static void ResetCurrentBlock(
        ref Utf16ValueStringBuilder errors,
        ref Utf16ValueStringBuilder warnings,
        HashSet<string> seenErrors,
        HashSet<string> seenWarnings,
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

        seenErrors.Clear();
        seenWarnings.Clear();
        errorCount = 0;
        warningCount = 0;
    }

    static bool AppendUniqueDiagnostic(
        ref Utf16ValueStringBuilder builder,
        HashSet<string> seenDiagnostics,
        string line,
        ref int count
    )
    {
        if (!seenDiagnostics.Add(line))
            return false;

        builder.AppendLine(line);
        count++;
        return true;
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

    internal static UnityProjectProcessInfo? FindMatchingProjectProcess(
        IReadOnlyList<UnityProjectProcessInfo> runningUnityProcesses,
        string projectPath
    )
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        foreach (var processInfo in runningUnityProcesses)
        {
            // import workers use the editor executable and repeat their parent's project path
            if (AuxiliaryUnityProcessArgumentRegex().IsMatch(processInfo.CommandLine ?? ""))
                continue;

            var processProjectPath = ConduitUtility.TryExtractProjectPathFromCommandLine(
                processInfo.CommandLine,
                EditorProjectPathArgumentRegex()
            );
            if (string.Equals(processProjectPath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
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
