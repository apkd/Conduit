#!/usr/bin/env dotnet

using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

var options = ParseArguments(args);
var repoRoot = ResolveRepoRoot(options.RepoRoot);
var projectPath = ResolveProjectPath(repoRoot, options.ProjectPath);
var resultsPath = ResolveResultsPath(options.ResultsPath);
var logPath = ResolveLogPath(options.LogPath);
var filter = string.IsNullOrWhiteSpace(options.Filter) ? "Conduit.ConduitMcpEndToEndTests" : options.Filter!;
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

    if (!options.NoKillUnity)
        await KillUnityAsync();

    var unityExitCode = await RunUnityTestsAsync(
        unityPath,
        projectPath,
        resultsPath,
        logPath,
        filter,
        unityTimeout,
        batchMode: !options.NoBatchMode);

    EnsureFileExists(resultsPath, "Unity test results");

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
    TimeSpan timeout,
    bool batchMode)
{
    var arguments = new List<string>
    {
        "-projectPath",
        ToWindowsPath(projectPath),
        "-executeMethod",
        "Conduit.CI.RunFilteredEditModeTestsFromCommandLine",
        "-conduitTestResults",
        ToWindowsPath(resultsPath),
        "-logFile",
        ToWindowsPath(logPath),
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
    return await RunProcessAsync(
        unityPath,
        arguments,
        workingDirectory: null,
        timeout,
        throwOnError: false);
}

static async Task KillUnityAsync()
{
    Console.Error.WriteLine("Killing live Unity.exe instances...");
    await RunProcessAsync(
        ResolveTaskkillPath(),
        ["/IM", "Unity.exe", "/F", "/T"],
        workingDirectory: null,
        timeout: TimeSpan.FromSeconds(30),
        throwOnError: false);
    await Task.Delay(TimeSpan.FromSeconds(2));
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
        var normalized = NormalizeToPlatformPath(overridePath);
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
        var normalized = NormalizeToPlatformPath(overridePath);
        EnsureDirectoryExists(normalized, "Unity project");
        return normalized;
    }

    var sibling = Path.GetFullPath(Path.Combine(repoRoot, "..", "ConduitPlayground"));
    EnsureDirectoryExists(sibling, "Default Unity project");
    return sibling;
}

static string ResolveResultsPath(string? overridePath)
{
    var path = string.IsNullOrWhiteSpace(overridePath)
        ? "/mnt/c/Users/apk/AppData/LocalLow/apkd/UnityConduit_Unity/TestResults.xml"
        : overridePath;

    return NormalizeToPlatformPath(path);
}

static string ResolveLogPath(string? overridePath)
{
    var path = string.IsNullOrWhiteSpace(overridePath)
        ? Path.Combine(Path.GetTempPath(), $"conduit-unity-e2e-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log")
        : overridePath;

    return NormalizeToPlatformPath(path);
}

static string ResolveUnityEditorPath(string projectPath, string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
        return NormalizeToPlatformPath(overridePath);

    var projectVersionPath = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
    var editorVersion = File.ReadLines(projectVersionPath)
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
        .Select(line => line["m_EditorVersion:".Length..].Trim())
        .FirstOrDefault();

    if (string.IsNullOrWhiteSpace(editorVersion))
        throw new InvalidOperationException($"Could not read the Unity editor version from '{projectVersionPath}'.");

    var candidates = new[]
    {
        NormalizeToPlatformPath($"/mnt/c/Program Files/Unity/Hub/Editor/{editorVersion}/Editor/Unity.exe"),
        NormalizeToPlatformPath("/mnt/c/Program Files/Unity/Editor/Unity.exe"),
        $@"C:\Program Files\Unity\Hub\Editor\{editorVersion}\Editor\Unity.exe",
        @"C:\Program Files\Unity\Editor\Unity.exe",
    }.Distinct(StringComparer.OrdinalIgnoreCase);

    var unityPath = candidates.FirstOrDefault(File.Exists);
    if (unityPath != null)
        return unityPath;

    throw new InvalidOperationException(
        $"Could not find Unity.exe for version '{editorVersion}'. Pass --unity <path> to override.");
}

static string NormalizeToPlatformPath(string path) =>
    UseWindowsPaths() ? ToWindowsPath(path) : ToWslPath(path);

static string ToWslPath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
        return path;

    var normalized = path.Replace('\\', '/');
    if (normalized.Length >= 3 && char.IsLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '/')
        return $"/mnt/{char.ToLowerInvariant(normalized[0])}/{normalized[3..]}";

    return normalized;
}

static string ToWindowsPath(string path)
{
    if (string.IsNullOrWhiteSpace(path))
        return path;

    var normalized = path.Replace('/', '\\');
    if (IsWindowsStylePath(normalized))
        return normalized;

    normalized = ToWslPath(path);
    if (normalized.StartsWith("/mnt/", StringComparison.Ordinal) && normalized.Length > 6 && normalized[6] == '/')
        return $"{char.ToUpperInvariant(normalized[5])}:{normalized[6..].Replace('/', '\\')}";

    return normalized.Replace('/', '\\');
}

static string ResolveTaskkillPath() =>
    UseWindowsPaths() ? "taskkill.exe" : "/mnt/c/Windows/System32/taskkill.exe";

static bool IsWindowsStylePath(string path) =>
    !string.IsNullOrWhiteSpace(path)
    && path.Length >= 3
    && char.IsLetter(path[0])
    && path[1] == ':'
    && (path[2] == '\\' || path[2] == '/');

static bool UseWindowsPaths() =>
    OperatingSystem.IsWindows() || IsWindowsStylePath(Environment.CurrentDirectory);

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
            case "--no-kill-unity":
                options.NoKillUnity = true;
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
    builder.AppendLine("  --project <path>    Unity project path. Defaults to ../ConduitPlayground.");
    builder.AppendLine("  --unity <path>      Unity.exe path. Defaults to the version from ProjectVersion.txt.");
    builder.AppendLine("  --filter <name>     Test filter. Defaults to Conduit.ConduitMcpEndToEndTests.");
    builder.AppendLine("  --results <path>    XML report path. Defaults to C:/Users/apk/AppData/LocalLow/apkd/UnityConduit_Unity/TestResults.xml.");
    builder.AppendLine("  --log <path>        Unity log path. Defaults to the platform temp directory.");
    builder.AppendLine("  --timeout <span>    Unity test timeout. Defaults to 00:10:00.");
    builder.AppendLine("  --no-batchmode      Run Unity without -batchmode/-nographics.");
    builder.AppendLine("  --skip-build        Skip the Conduit.Server Debug build prerequisite.");
    builder.AppendLine("  --no-kill-unity     Do not kill live Unity.exe processes before the run.");
    builder.AppendLine("  --help              Print this help.");
    Console.Error.Write(builder.ToString());
}

sealed class RunnerOptions
{
    public string? RepoRoot { get; set; }
    public string? ProjectPath { get; set; }
    public string? UnityPath { get; set; }
    public string? ResultsPath { get; set; }
    public string? LogPath { get; set; }
    public string? Filter { get; set; }
    public TimeSpan? Timeout { get; set; }
    public bool NoBatchMode { get; set; }
    public bool SkipBuild { get; set; }
    public bool NoKillUnity { get; set; }
}

sealed record TestSummary(
    string Name,
    string Result,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    string? Reason);
