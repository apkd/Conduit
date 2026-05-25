#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

var options = ParseArguments(args);
var repoRoot = ResolveRepoRoot(options.RepoRoot);
var projectPath = ResolveProjectPath(repoRoot, options.ProjectPath);
var resultsPath = ResolveResultsPath(options.ResultsPath);
var logPath = ResolveLogPath(options.LogPath);
var filter = string.IsNullOrWhiteSpace(options.Filter) ? "ConduitMcpEndToEndTests" : options.Filter!;
var unityTimeout = options.Timeout ?? TimeSpan.FromMinutes(10);

try
{
    var unityPath = ResolveUnityEditorPath(projectPath, options.UnityPath);

    EnsureFileExists(unityPath, "Unity editor executable");
    EnsureFileExists(Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt"), "Unity project version file");

    Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    DeleteIfExists(resultsPath);
    DeleteIfExists(logPath);

    if (!options.SkipBuild)
        await RunCheckedAsync(
            "dotnet",
            ["build", "Conduit.Server/Conduit.csproj", "-c", "Debug", "-v", "minimal"],
            repoRoot,
            timeout: TimeSpan.FromMinutes(5));

    if (options.KillUnity)
        await KillUnityAsync(projectPath);

    var unityExitCode = await RunUnityTestsAsync(
        unityPath,
        projectPath,
        resultsPath,
        logPath,
        filter,
        repoRoot,
        unityTimeout,
        batchMode: !options.NoBatchMode,
        unityWrapper: options.UnityWrapper);

    if (!File.Exists(resultsPath))
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Unity exit code: {unityExitCode}");
        Console.Error.WriteLine($"Results path: {resultsPath}");
        Console.Error.WriteLine($"Log path: {logPath}");
        PrintLogTail(logPath);
        Console.Error.WriteLine("Unity test results were not produced.");
        Environment.ExitCode = 1;
        return;
    }

    var xml = await File.ReadAllTextAsync(resultsPath);
    Console.Out.Write(xml);

    var summary = ReadSummary(resultsPath, filter);
    var succeeded = unityExitCode == 0 && summary.Failed == 0 && summary.Skipped == 0 && summary.Passed > 0;
    if (!succeeded)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Unity exit code: {unityExitCode}");
        Console.Error.WriteLine($"Target: {summary.Name}");
        Console.Error.WriteLine($"Result: {summary.Result}");
        Console.Error.WriteLine($"Passed: {summary.Passed}, Failed: {summary.Failed}, Skipped: {summary.Skipped}, Total: {summary.Total}");
        if (!string.IsNullOrWhiteSpace(summary.Reason))
            Console.Error.WriteLine($"Reason: {summary.Reason}");

        PrintLogTail(logPath);
        Environment.ExitCode = 1;
    }
}
catch (Exception exception)
{
    PrintLogTail(logPath);
    Console.Error.WriteLine();
    Console.Error.WriteLine(exception.Message);
    Environment.ExitCode = 1;
}

return;

static async Task<int> RunUnityTestsAsync(
    string unityPath,
    string projectPath,
    string resultsPath,
    string logPath,
    string filter,
    string repoRoot,
    TimeSpan timeout,
    bool batchMode,
    string? unityWrapper)
{
    var arguments = new List<string>
    {
        "-projectPath",
        projectPath,
        "-executeMethod",
        "Conduit.CI.RunFilteredEditModeTestsFromCommandLine",
        "-conduitTestResults",
        resultsPath,
        "-logFile",
        logPath,
    };

    if (batchMode)
    {
        arguments.Insert(0, "-nographics");
        arguments.Insert(0, "-batchmode");
    }

    if (!string.IsNullOrWhiteSpace(filter))
    {
        arguments.Add("-conduitTestFilter");
        arguments.Add(filter);
    }

    Console.Error.WriteLine(
        $"Running Unity EditMode tests through Conduit.CI with filter '{filter}' ({(batchMode ? "batchmode" : "interactive mode")})...");
    var invocation = BuildUnityInvocation(unityPath, arguments, unityWrapper);
    return await RunProcessAsync(
        invocation.FileName,
        invocation.Arguments,
        workingDirectory: null,
        timeout,
        throwOnError: false,
        environment: BuildUnityEnvironment(repoRoot));
}

static UnityInvocation BuildUnityInvocation(string unityPath, IReadOnlyList<string> unityArguments, string? unityWrapper)
{
    if (string.IsNullOrWhiteSpace(unityWrapper) || string.Equals(unityWrapper, "none", StringComparison.OrdinalIgnoreCase))
        return new(unityPath, unityArguments);

    var wrapper = ResolveUnityWrapper(unityWrapper);
    if (string.IsNullOrWhiteSpace(wrapper))
        return new(unityPath, unityArguments);

    var arguments = new List<string> { unityPath };
    arguments.AddRange(unityArguments);
    Console.Error.WriteLine($"Launching Unity through NixOS wrapper: {wrapper}");
    return new(wrapper, arguments);
}

static string? ResolveUnityWrapper(string unityWrapper)
{
    if (string.Equals(unityWrapper, "auto", StringComparison.OrdinalIgnoreCase))
        return FindUnityHubFhsEnv() ?? FindExecutableOnPath("steam-run");

    if (unityWrapper.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || Path.IsPathRooted(unityWrapper))
        return NormalizePath(unityWrapper);

    return FindExecutableOnPath(unityWrapper) ?? unityWrapper;
}

static Dictionary<string, string?> BuildUnityEnvironment(string repoRoot)
{
    var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
    environment["CONDUIT_REPO_ROOT"] = repoRoot;
    SetIfMissing(environment, "GIO_USE_VFS", "local");
    SetIfMissing(environment, "GTK_USE_PORTAL", "0");
    SetIfMissing(environment, "GSETTINGS_BACKEND", "memory");
    SetIfMissing(environment, "NO_AT_BRIDGE", "1");
    SetIfMissing(environment, "NIX_XDG_DESKTOP_PORTAL_DIR", ResolveNixXdgDesktopPortalDirectory("/run/current-system/sw"));
    SetIfMissing(environment, "GIO_EXTRA_MODULES", ResolveNixGioExtraModules("/run/current-system/sw"));
    return environment;
}

static void SetIfMissing(Dictionary<string, string?> environment, string name, string? value)
{
    if (string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
        return;

    environment[name] = value;
}

static async Task KillUnityAsync(string projectPath)
{
    Console.Error.WriteLine("Stopping live Unity instances for this project...");
    await RunProcessAsync(
        "pkill",
        ["-f", $"Unity.*-projectPath[ =]+{RegexEscapeForPkill(projectPath)}"],
        workingDirectory: null,
        timeout: TimeSpan.FromSeconds(30),
        throwOnError: false);
    await Task.Delay(TimeSpan.FromSeconds(2));
}

static string RegexEscapeForPkill(string value)
{
    var builder = new StringBuilder();
    foreach (var character in value)
    {
        if ("\\.^$|?*+()[]{}".IndexOf(character) >= 0)
            builder.Append('\\');

        builder.Append(character);
    }

    return builder.ToString();
}

static async Task RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan? timeout)
{
    Console.Error.WriteLine($"Running: {fileName} {string.Join(' ', arguments)}");
    var exitCode = await RunProcessAsync(fileName, arguments, workingDirectory, timeout, throwOnError: false);
    if (exitCode == 0)
        return;

    throw new InvalidOperationException($"Command '{fileName}' exited with code {exitCode}.");
}

static async Task<int> RunProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string? workingDirectory,
    TimeSpan? timeout,
    bool throwOnError,
    IReadOnlyDictionary<string, string?>? environment = null)
{
    using var process = new Process
    {
        StartInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        },
        EnableRaisingEvents = true,
    };

    foreach (var argument in arguments)
        process.StartInfo.ArgumentList.Add(argument);

    if (environment != null)
    {
        foreach (var (key, value) in environment)
            process.StartInfo.Environment[key] = value;
    }

    if (!process.Start())
        throw new InvalidOperationException($"Failed to start '{fileName}'.");

    var stdoutTask = PumpToErrorAsync(process.StandardOutput);
    var stderrTask = PumpToErrorAsync(process.StandardError);
    try
    {
        using var cancellation = timeout is { } value ? new CancellationTokenSource(value) : null;
        await process.WaitForExitAsync(cancellation?.Token ?? CancellationToken.None);
    }
    catch (OperationCanceledException)
    {
        TryKillProcessTree(process);
        throw new TimeoutException($"Command '{fileName}' timed out after {timeout}.");
    }

    await Task.WhenAll(stdoutTask, stderrTask);

    if (throwOnError && process.ExitCode != 0)
        throw new InvalidOperationException($"Command '{fileName}' exited with code {process.ExitCode}.");

    return process.ExitCode;
}

static void TryKillProcessTree(Process process)
{
    try
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }
    catch
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch { }
    }
}

static async Task PumpToErrorAsync(StreamReader reader)
{
    while (await reader.ReadLineAsync() is { } line)
        Console.Error.WriteLine(line);
}

static void PrintLogTail(string logPath)
{
    if (!File.Exists(logPath))
        return;

    Console.Error.WriteLine();
    Console.Error.WriteLine($"Log tail: {logPath}");
    foreach (var line in ReadLastLines(logPath, 80))
        Console.Error.WriteLine(line);
}

static IEnumerable<string> ReadLastLines(string path, int maxLines)
{
    var queue = new Queue<string>(maxLines);
    foreach (var line in EnumerateLines(path))
    {
        if (queue.Count == maxLines)
            queue.Dequeue();

        queue.Enqueue(line);
    }

    return queue;
}

static IEnumerable<string> EnumerateLines(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
        yield return line;
}

static TestSummary ReadSummary(string resultsPath, string filter)
{
    var document = XDocument.Load(resultsPath);
    var target = FindSummaryNode(document, filter);

    return new(
        Name: (string?)target.Attribute("fullname")
              ?? (string?)target.Attribute("classname")
              ?? (string?)target.Attribute("name")
              ?? "<unknown>",
        Result: (string?)target.Attribute("result") ?? "<unknown>",
        Total: ReadIntAttribute(target, "total"),
        Passed: ReadIntAttribute(target, "passed"),
        Failed: ReadIntAttribute(target, "failed"),
        Skipped: ReadIntAttribute(target, "skipped"),
        Reason: target.Element("reason")?.Element("message")?.Value?.Trim());

    static int ReadIntAttribute(XElement element, string name) =>
        int.TryParse((string?)element.Attribute(name), out var value) ? value : 0;
}

static XElement FindSummaryNode(XDocument document, string filter)
    => document
           .Descendants("test-suite")
           .FirstOrDefault(node =>
               (string?)node.Attribute("fullname") == filter
               || (string?)node.Attribute("classname") == filter
               || (string?)node.Attribute("name") == filter)
       ?? document.Root
       ?? throw new InvalidOperationException("Could not read the test summary XML.");

static string ResolveRepoRoot(string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        var normalized = NormalizePath(overridePath);
        EnsureDirectoryExists(normalized, "Repository root");
        return normalized;
    }

    var current = new DirectoryInfo(Environment.CurrentDirectory);
    while (current != null)
    {
        var serverProjectPath = Path.Combine(current.FullName, "Conduit.Server", "Conduit.csproj");
        if (File.Exists(serverProjectPath))
            return current.FullName;

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not locate the UnityConduit repository root from the current directory. Pass --repo <path>.");
}

static string ResolveProjectPath(string repoRoot, string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        var normalized = NormalizePath(overridePath);
        EnsureDirectoryExists(normalized, "Unity project");
        return normalized;
    }

    foreach (var siblingName in new[] { "ConduitPlayground", "conduit-test" })
    {
        var sibling = Path.GetFullPath(Path.Combine(repoRoot, "..", siblingName));
        if (IsUnityProject(sibling))
            return sibling;
    }

    throw new DirectoryNotFoundException(
        $"Default Unity project was not found next to '{repoRoot}'. Pass --project <path>.");
}

static string ResolveResultsPath(string? overridePath)
{
    var path = string.IsNullOrWhiteSpace(overridePath)
        ? Path.Combine(Path.GetTempPath(), $"conduit-unity-e2e-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-results.xml")
        : overridePath;

    return NormalizePath(path);
}

static string ResolveLogPath(string? overridePath)
{
    var path = string.IsNullOrWhiteSpace(overridePath)
        ? Path.Combine(Path.GetTempPath(), $"conduit-unity-e2e-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log")
        : overridePath;

    return NormalizePath(path);
}

static string ResolveUnityEditorPath(string projectPath, string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
        return NormalizePath(overridePath);

    var projectVersionPath = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
    var editorVersion = File.ReadLines(projectVersionPath)
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
        .Select(line => line["m_EditorVersion:".Length..].Trim())
        .FirstOrDefault();

    if (string.IsNullOrWhiteSpace(editorVersion))
        throw new InvalidOperationException($"Could not read the Unity editor version from '{projectVersionPath}'.");

    var candidates = EnumerateUnityEditorPathCandidates(editorVersion)
        .Select(NormalizePath)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    var unityPath = candidates.FirstOrDefault(File.Exists);
    if (unityPath != null)
        return unityPath;

    throw new InvalidOperationException(
        $"Could not find the Unity editor for version '{editorVersion}'. Pass --unity <path> to override.");
}

static IEnumerable<string> EnumerateUnityEditorPathCandidates(string editorVersion)
{
    if (Environment.GetEnvironmentVariable("UNITY_EDITOR") is { Length: > 0 } unityEditor)
        yield return unityEditor;

    if (Environment.GetEnvironmentVariable("UNITY_PATH") is { Length: > 0 } unityPath)
        yield return unityPath;

    if (Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
    {
        yield return Path.Combine(home, "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
        yield return Path.Combine(home, ".local", "share", "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
    }

    yield return Path.Combine("/opt", "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
    yield return Path.Combine("/opt", "unity", editorVersion, "Editor", "Unity");

    if (FindExecutableOnPath("unity-editor") is { Length: > 0 } unityEditorPath)
        yield return unityEditorPath;

    if (FindExecutableOnPath("Unity") is { Length: > 0 } unityOnPath)
        yield return unityOnPath;
}

static string NormalizePath(string path) =>
    Path.GetFullPath(path);

static bool IsUnityProject(string path) =>
    Directory.Exists(path)
    && File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"))
    && File.Exists(Path.Combine(path, "Packages", "manifest.json"));

static string? FindExecutableOnPath(string executableName)
{
    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
        return null;

    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var candidate = Path.Combine(directory, executableName);
        if (File.Exists(candidate))
            return candidate;
    }

    return null;
}

static string? FindUnityHubFhsEnv()
{
    if (FindExecutableOnPath("unityhub-fhs-env") is { Length: > 0 } directPath)
        return directPath;

    var unityHubPath = FindExecutableOnPath("unityhub");
    if (string.IsNullOrWhiteSpace(unityHubPath))
        return null;

    try
    {
        return TryExtractUnityHubFhsEnvPath(File.ReadAllText(unityHubPath));
    }
    catch
    {
        return null;
    }
}

static string? TryExtractUnityHubFhsEnvPath(string? wrapperText)
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

static string? ResolveNixXdgDesktopPortalDirectory(string systemProfilePath)
{
    var candidates = new[]
    {
        Path.Combine(systemProfilePath, "share", "xdg-desktop-portal", "portals"),
        Path.Combine(systemProfilePath, "share", "xdg-desktop-portal"),
    };

    return candidates.FirstOrDefault(Directory.Exists);
}

static string? ResolveNixGioExtraModules(string systemProfilePath)
{
    var moduleRoot = Path.Combine(systemProfilePath, "lib", "gio", "modules");
    return Directory.Exists(moduleRoot) ? moduleRoot : null;
}

static void EnsureFileExists(string path, string description)
{
    if (!File.Exists(path))
        throw new FileNotFoundException($"{description} was not found.", path);
}

static void EnsureDirectoryExists(string path, string description)
{
    if (!Directory.Exists(path))
        throw new DirectoryNotFoundException($"{description} was not found: '{path}'.");
}

static void DeleteIfExists(string path)
{
    if (File.Exists(path))
        File.Delete(path);
}

static RunnerOptions ParseArguments(string[] arguments)
{
    var options = new RunnerOptions();

    for (var index = 0; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--repo":
                options.RepoRoot = ReadValue(arguments, ref index, "--repo");
                break;
            case "--project":
                options.ProjectPath = ReadValue(arguments, ref index, "--project");
                break;
            case "--unity":
                options.UnityPath = ReadValue(arguments, ref index, "--unity");
                break;
            case "--unity-wrapper":
                options.UnityWrapper = ReadValue(arguments, ref index, "--unity-wrapper");
                break;
            case "--results":
                options.ResultsPath = ReadValue(arguments, ref index, "--results");
                break;
            case "--log":
                options.LogPath = ReadValue(arguments, ref index, "--log");
                break;
            case "--filter":
                options.Filter = ReadValue(arguments, ref index, "--filter");
                break;
            case "--skip-build":
                options.SkipBuild = true;
                break;
            case "--kill-unity":
                options.KillUnity = true;
                break;
            case "--help":
            case "-h":
                PrintUsage();
                Environment.Exit(0);
                break;
            case "--timeout":
                options.Timeout = ParseTimeout(ReadValue(arguments, ref index, "--timeout"));
                break;
            case "--no-batchmode":
                options.NoBatchMode = true;
                break;
            default:
                throw new InvalidOperationException($"Unknown argument '{arguments[index]}'. Pass --help for usage.");
        }
    }

    return options;

    static string ReadValue(string[] source, ref int index, string option)
    {
        if (index + 1 >= source.Length)
            throw new InvalidOperationException($"Missing value for '{option}'.");

        index++;
        return source[index];
    }

    static TimeSpan ParseTimeout(string value) =>
        TimeSpan.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Invalid timeout '{value}'. Use a TimeSpan such as 00:05:00.");
}

static void PrintUsage()
{
    var builder = new StringBuilder();
    builder.AppendLine("Usage:");
    builder.AppendLine("  dotnet run --file run-unity-mcp-e2e.cs -- [options]");
    builder.AppendLine();
    builder.AppendLine("Options:");
    builder.AppendLine("  --repo <path>       Repository root. Defaults to walking up from the current directory.");
    builder.AppendLine("  --project <path>    Unity project path. Defaults to ../ConduitPlayground, then ../conduit-test.");
    builder.AppendLine("  --unity <path>      Unity editor path. Defaults to the version from ProjectVersion.txt.");
    builder.AppendLine("  --unity-wrapper <x> Optional Unity launcher wrapper. Use none, auto, or a wrapper path.");
    builder.AppendLine("                      Defaults to auto on NixOS and none elsewhere.");
    builder.AppendLine("  --filter <name>     Test filter. Defaults to ConduitMcpEndToEndTests.");
    builder.AppendLine("  --results <path>    XML report path. Defaults to the platform temp directory.");
    builder.AppendLine("  --log <path>        Unity log path. Defaults to the platform temp directory.");
    builder.AppendLine("  --timeout <span>    Unity test timeout. Defaults to 00:10:00.");
    builder.AppendLine("  --no-batchmode      Run Unity without -batchmode/-nographics.");
    builder.AppendLine("  --skip-build        Skip the Conduit.Server Debug build prerequisite.");
    builder.AppendLine("  --kill-unity        Stop live Unity processes for the project before the run.");
    builder.AppendLine("  --help              Print this help.");
    Console.Error.Write(builder.ToString());
}

sealed class RunnerOptions
{
    public string? RepoRoot { get; set; }
    public string? ProjectPath { get; set; }
    public string? UnityPath { get; set; }
    public string? UnityWrapper { get; set; } = OperatingSystem.IsLinux() && File.Exists("/etc/NIXOS") ? "auto" : "none";
    public string? ResultsPath { get; set; }
    public string? LogPath { get; set; }
    public string? Filter { get; set; }
    public TimeSpan? Timeout { get; set; }
    public bool NoBatchMode { get; set; }
    public bool SkipBuild { get; set; }
    public bool KillUnity { get; set; }
}

sealed record UnityInvocation(string FileName, IReadOnlyList<string> Arguments);

sealed record TestSummary(
    string Name,
    string Result,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    string? Reason);
