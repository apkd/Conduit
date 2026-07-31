using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var options = Options.Parse(args);
var testRoot = Path.Combine(
    Path.GetTempPath(),
    "conduit-player-e2e-" + Environment.ProcessId
);
var ipcRoot = options.AttachIpcRoot ?? Path.Combine(testRoot, "ipc");
Directory.CreateDirectory(ipcRoot);

Process? player = null;
McpClient? client = null;
var endpoints = new List<PlayerEndpoint>();
try
{
    if (options.AttachIpcRoot == null)
        player = StartPlayer(options, ipcRoot);
    var endpoint = await WaitForPlayerAsync(
        ipcRoot,
        static _ => true,
        TimeSpan.FromMinutes(2)
    );
    endpoints.Add(endpoint);
    Console.WriteLine(
        $"Discovered {endpoint.Selector} ({endpoint.Platform}, {endpoint.UnityVersion})."
    );

    client = await McpClient.StartAsync(options.Server, ipcRoot);
    var status = await client.CallAsync(
        "status",
        new() { ["projectPath"] = endpoint.Selector }
    );
    RequireContains(status, endpoint.Selector, "player status selector");
    RequireContains(status, "Status: player", "player status mode");

    var projects = new[]
    {
        CreateAssociationProject(testRoot, "project-copy-a", endpoint),
        CreateAssociationProject(testRoot, "project-copy-b", endpoint),
    };
    foreach (var project in projects)
    {
        var projectStatus = await client.CallAsync(
            "status",
            new() { ["projectPath"] = project }
        );
        RequireContains(
            projectStatus,
            $"LIVE PLAYER PROCESS ID: `{endpoint.Selector}`",
            "project/player association"
        );
    }

    var help = await client.CallAsync(
        "help",
        new() { ["projectPath"] = endpoint.Selector }
    );
    RequireContains(help, "loaded objects", "player help");

    var search = await client.CallAsync(
        "search",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["query"] = "t:Camera",
        }
    );
    RequireContains(search, "Main Camera", "player search");
    if (search.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 1)
        throw new InvalidOperationException(
            $"Type-filtered player search returned unexpected objects:\n{search}"
        );

    var show = await client.CallAsync(
        "show",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["query"] = "/Main Camera",
        }
    );
    RequireContains(show, "/Main Camera", "player show");

    var json = await client.CallAsync(
        "to_json",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["query"] = "/Main Camera",
        }
    );
    RequireContains(json, "{", "player JSON");

    var overwrite = await client.CallAsync(
        "from_json_overwrite",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["query"] = "/Main Camera",
            ["json"] = "{}",
        }
    );
    RequireContains(overwrite, "Updated Main Camera", "player JSON overwrite");

    var reflection = await client.CallAsync(
        "reflect",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["mode"] = "types",
            ["type"] = "UnityEngine.Camera",
        }
    );
    RequireContains(reflection, "UnityEngine.Camera", "player reflection");

    var firstSnippet = await client.CallAsync(
        "execute_code",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["snippet"] =
                "return Application.productName + \"-\" + (2 + 3);",
        },
        TimeSpan.FromMinutes(5)
    );
    RequireContains(firstSnippet, "-5", "compiled player snippet");
    var snippetName = Regex.Match(firstSnippet, @"NAME: `(?<name>\d+\.cs)`")
        .Groups["name"]
        .Value;
    if (snippetName.Length == 0)
        throw new InvalidOperationException(
            $"The compiled snippet did not return a reusable name:\n{firstSnippet}"
        );

    var repeatedSnippet = await client.CallAsync(
        "execute_code",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["snippet"] = snippetName,
        }
    );
    RequireContains(repeatedSnippet, "-5", "reused player snippet");

    var inferredNamespace = await client.CallAsync(
        "execute_code",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["snippet"] = "return SceneManager.GetActiveScene().name;",
        }
    );
    RequireContains(
        inferredNamespace,
        "BridgeFixtureScene",
        "player namespace inference"
    );

    var asynchronousSnippet = await client.CallAsync(
        "execute_code",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["snippet"] = "await Task.Delay(1); return 7;",
        }
    );
    RequireContains(asynchronousSnippet, "7", "async player snippet");

    var screenshot = await client.CallAsync(
        "screenshot",
        new()
        {
            ["projectPath"] = endpoint.Selector,
            ["target"] = "game_view",
        }
    );
    const string headlessScreenshotDiagnostic =
        "Player screenshots require an interactive Unity player with a graphics device.";
    if (options.ExpectScreenshotUnavailable)
    {
        RequireContains(
            screenshot,
            "ERROR: Unity built-in modules `com.unity.modules.imageconversion` and " +
            "`com.unity.modules.screencapture` are not enabled in this project.",
            "player screenshot module diagnostic"
        );
    }
    else if (options.AttachIpcRoot == null)
    {
        RequireContains(
            screenshot,
            headlessScreenshotDiagnostic,
            "headless player screenshot diagnostic"
        );
    }
    else if (!screenshot.Contains(headlessScreenshotDiagnostic, StringComparison.Ordinal))
    {
        var screenshotPath = Regex.Match(
            screenshot,
            @"^Player image captured:\s*(?<path>[^\r\n]+)",
            RegexOptions.Multiline
        ).Groups["path"].Value;
        if (!File.Exists(screenshotPath)
            || File.ReadAllBytes(screenshotPath) is not
                [0x89, 0x50, 0x4e, 0x47, ..])
            throw new InvalidOperationException(
                $"The player screenshot was neither rejected as headless nor materialized as PNG: {screenshot}"
            );
    }

    var editorOnly = await client.CallAsync(
        "playmode",
        new() { ["projectPath"] = endpoint.Selector }
    );
    RequireContains(
        editorOnly,
        "The tool `playmode` is editor-only.",
        "editor-only diagnostic"
    );

    var profilerDiagnostic = await client.CallAsync(
        "profiler_overview",
        new() { ["projectPath"] = endpoint.Selector }
    );
    RequireContains(
        profilerDiagnostic,
        $"No matching Unity Editor is currently profiling {endpoint.Selector}.",
        "profiler routing diagnostic"
    );

    var restart = await client.CallAsync(
        "restart",
        new() { ["projectPath"] = endpoint.Selector },
        TimeSpan.FromMinutes(2)
    );
    var replacementSelector = Regex.Match(
        restart,
        @"LIVE PLAYER PROCESS ID: `(?<selector>player:\d+)`"
    ).Groups["selector"].Value;
    if (replacementSelector.Length == 0 || replacementSelector == endpoint.Selector)
        throw new InvalidOperationException(
            $"Player restart did not return a replacement selector:\n{restart}"
        );

    var replacement = await WaitForPlayerAsync(
        ipcRoot,
        value => value.Selector == replacementSelector,
        TimeSpan.FromMinutes(1)
    );
    endpoints.Add(replacement);
    var replacementStatus = await client.CallAsync(
        "status",
        new() { ["projectPath"] = replacement.Selector }
    );
    RequireContains(
        replacementStatus,
        replacement.Selector,
        "replacement player status"
    );

    Console.WriteLine("Unity player MCP E2E passed.");
}
catch
{
    if (player != null)
    {
        var output = PlayerOutputCapture.Read(player);
        if (output.Length > 0)
            Console.Error.WriteLine(
                $"Player output (last 200 lines):\n{output}"
            );
    }

    if (client != null)
    {
        var errors = client.ReadCapturedErrors();
        if (errors.Length > 0)
            Console.Error.WriteLine(
                $"Server output (last 200 lines):\n{errors}"
            );
    }

    throw;
}
finally
{
    if (client != null)
        await client.DisposeAsync();

    if (options.AttachIpcRoot == null && player is { HasExited: false })
        try
        {
            player.Kill(entireProcessTree: true);
            await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch { }

    if (options.AttachIpcRoot == null)
        foreach (var endpoint in endpoints.Where(static value => value.CanMonitorProcess))
            try
            {
                using var process = Process.GetProcessById(endpoint.ProcessId);
                process.Kill(entireProcessTree: true);
            }
            catch { }

    player?.Dispose();
    try
    {
        if (Directory.Exists(testRoot))
            Directory.Delete(testRoot, recursive: true);
    }
    catch { }
}

static Process StartPlayer(Options options, string ipcRoot)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = options.Launcher ?? options.Player!,
        WorkingDirectory = Path.GetDirectoryName(options.Player!)!,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var argument in options.LauncherArguments)
        startInfo.ArgumentList.Add(argument);
    if (options.Launcher != null)
        startInfo.ArgumentList.Add(options.Player!);
    startInfo.ArgumentList.Add("-batchmode");
    startInfo.ArgumentList.Add("-logFile");
    startInfo.ArgumentList.Add("-");
    startInfo.Environment["CONDUIT_IPC_ROOT"] = ipcRoot;

    var process = new Process { StartInfo = startInfo };
    process.Start();
    PlayerOutputCapture.Start(process);
    return process;
}

static async Task<PlayerEndpoint> WaitForPlayerAsync(
    string ipcRoot,
    Func<PlayerEndpoint, bool> predicate,
    TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var endpointRoot = Path.Combine(ipcRoot, "endpoints");
        if (Directory.Exists(endpointRoot))
            foreach (var path in Directory.EnumerateFiles(
                         endpointRoot,
                         "endpoint.json",
                         SearchOption.AllDirectories
                     ))
                try
                {
                    var json = JsonNode.Parse(await File.ReadAllTextAsync(path));
                    if (json?["endpoint_kind"]?.GetValue<string>() != "player")
                        continue;

                    var endpoint = new PlayerEndpoint(
                        json["process_id"]!.GetValue<int>(),
                        json["session_instance_id"]!.GetValue<string>(),
                        json["unity_version"]!.GetValue<string>(),
                        json["platform"]!.GetValue<string>(),
                        json["cloud_project_id"]?.GetValue<string>() ?? "",
                        json["company_name"]?.GetValue<string>() ?? "",
                        json["product_name"]?.GetValue<string>() ?? "",
                        json["can_monitor_process"]?.GetValue<bool>() ?? false
                    );
                    if (predicate(endpoint))
                        return endpoint;
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException
                    or System.Text.Json.JsonException) { }

        await Task.Delay(100);
    }

    throw new TimeoutException(
        $"No matching Unity player advertised itself under '{ipcRoot}' within {timeout}."
    );
}

static string CreateAssociationProject(
    string testRoot,
    string name,
    PlayerEndpoint endpoint)
{
    var project = Path.Combine(testRoot, name);
    Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
    Directory.CreateDirectory(Path.Combine(project, "Packages"));
    File.WriteAllText(
        Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"),
        $"m_EditorVersion: {endpoint.UnityVersion}\n"
    );
    File.WriteAllText(
        Path.Combine(project, "ProjectSettings", "ProjectSettings.asset"),
        $"""
         PlayerSettings:
           companyName: {endpoint.CompanyName}
           productName: {endpoint.ProductName}
           cloudProjectId: {endpoint.CloudProjectId}
         """
    );
    File.WriteAllText(
        Path.Combine(project, "Packages", "manifest.json"),
        """{"dependencies":{"dev.tryfinally.conduit":"file:."}}"""
    );
    return project;
}

static void RequireContains(string actual, string expected, string context)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException(
            $"{context} did not contain '{expected}':\n{actual}"
        );
}

sealed class McpClient : IAsyncDisposable
{
    readonly Process process;
    readonly ConcurrentQueue<string> stderr = new();
    readonly Task stderrCapture;
    int nextRequestId;

    McpClient(Process process)
    {
        this.process = process;
        stderrCapture = CaptureErrorsAsync(process, stderr);
    }

    public static async Task<McpClient> StartAsync(
        string server,
        string ipcRoot)
    {
        var startInfo = new ProcessStartInfo(server)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["CONDUIT_IPC_ROOT"] = ipcRoot;
        var process = new Process { StartInfo = startInfo };
        process.Start();
        var client = new McpClient(process);
        await client.RequestAsync(
            "initialize",
            new()
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "conduit-player-e2e",
                    ["version"] = "0",
                },
            },
            TimeSpan.FromSeconds(20)
        );
        await client.SendAsync(
            new()
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
                ["params"] = new JsonObject(),
            }
        );
        return client;
    }

    public async Task<string> CallAsync(
        string tool,
        JsonObject arguments,
        TimeSpan? timeout = null,
        bool expectToolError = false)
    {
        var response = await RequestAsync(
            "tools/call",
            new()
            {
                ["name"] = tool,
                ["arguments"] = arguments,
            },
            timeout ?? TimeSpan.FromMinutes(2)
        );
        var result = response["result"]?.AsObject()
                     ?? throw new InvalidOperationException(
                         $"Tool '{tool}' returned no MCP result: {response}"
                     );
        var isError = result["isError"]?.GetValue<bool>() ?? false;
        if (isError != expectToolError)
            throw new InvalidOperationException(
                $"Tool '{tool}' returned isError={isError}: {response}"
            );

        return result["content"]?[0]?["text"]?.GetValue<string>()
               ?? throw new InvalidOperationException(
                   $"Tool '{tool}' returned no text content: {response}"
               );
    }

    async Task<JsonObject> RequestAsync(
        string method,
        JsonObject parameters,
        TimeSpan timeout)
    {
        var requestId = Interlocked.Increment(ref nextRequestId);
        await SendAsync(
            new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method,
                ["params"] = parameters,
            }
        );

        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(
                cancellation.Token
            );
            if (line == null)
                throw new IOException(
                    $"The MCP server exited before response {requestId}."
                );

            if (JsonNode.Parse(line) is JsonObject response
                && response["id"]?.GetValue<int>() == requestId)
                return response;
        }
    }

    async Task SendAsync(JsonObject payload)
    {
        await process.StandardInput.WriteLineAsync(payload.ToJsonString());
        await process.StandardInput.FlushAsync();
    }

    public string ReadCapturedErrors() =>
        string.Join(Environment.NewLine, stderr.TakeLast(200));

    public async ValueTask DisposeAsync()
    {
        try { process.StandardInput.Close(); } catch { }
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        try { await stderrCapture.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

        process.Dispose();
    }

    static async Task CaptureErrorsAsync(
        Process process,
        ConcurrentQueue<string> lines)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            lines.Enqueue(line);
            if (lines.Count > 1000)
                for (var index = 0; index < 200; index++)
                    lines.TryDequeue(out _);
        }
    }
}

static class PlayerOutputCapture
{
    static readonly ConcurrentDictionary<int, List<string>> linesByProcess
        = new();

    public static void Start(Process process)
    {
        var lines = linesByProcess.GetOrAdd(process.Id, static _ => new());
        _ = CaptureAsync(process.StandardOutput, lines);
        _ = CaptureAsync(process.StandardError, lines);
    }

    public static string Read(Process process)
    {
        if (!linesByProcess.TryGetValue(process.Id, out var lines))
            return string.Empty;

        lock (lines)
            return string.Join(Environment.NewLine, lines.TakeLast(200));
    }

    static async Task CaptureAsync(
        StreamReader reader,
        List<string> lines)
    {
        while (await reader.ReadLineAsync() is { } line)
            lock (lines)
            {
                lines.Add(line);
                if (lines.Count > 1000)
                    lines.RemoveRange(0, 200);
            }
    }
}

sealed record PlayerEndpoint(
    int ProcessId,
    string SessionInstanceId,
    string UnityVersion,
    string Platform,
    string CloudProjectId,
    string CompanyName,
    string ProductName,
    bool CanMonitorProcess)
{
    public string Selector => "player:" + ProcessId;
}

sealed record Options(
    string Server,
    string? Player,
    string? Launcher,
    string[] LauncherArguments,
    string? AttachIpcRoot,
    bool ExpectScreenshotUnavailable)
{
    public static Options Parse(string[] arguments)
    {
        string? server = null;
        string? player = null;
        string? launcher = null;
        string? attachIpcRoot = null;
        bool expectScreenshotUnavailable = false;
        var launcherArguments = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            var value = index + 1 < arguments.Length
                ? arguments[index + 1]
                : null;
            switch (arguments[index])
            {
                case "--server" when value != null:
                    server = Path.GetFullPath(value);
                    index++;
                    break;
                case "--player" when value != null:
                    player = Path.GetFullPath(value);
                    index++;
                    break;
                case "--launcher" when value != null:
                    launcher = Path.GetFullPath(value);
                    index++;
                    break;
                case "--launcher-arg" when value != null:
                    launcherArguments.Add(value);
                    index++;
                    break;
                case "--attach-ipc-root" when value != null:
                    attachIpcRoot = Path.GetFullPath(value);
                    index++;
                    break;
                case "--expect-screenshot-unavailable":
                    expectScreenshotUnavailable = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported or incomplete argument '{arguments[index]}'."
                    );
            }
        }

        if (server == null || player == null && attachIpcRoot == null)
            throw new ArgumentException(
                "Usage: --server <path> (--player <path> | --attach-ipc-root <path>) "
                + "[--launcher <path> --launcher-arg <value> ...] "
                + "[--expect-screenshot-unavailable]"
            );
        if (!File.Exists(server))
            throw new FileNotFoundException("Conduit server not found.", server);
        if (player != null && !File.Exists(player))
            throw new FileNotFoundException("Unity player not found.", player);
        if (player != null && attachIpcRoot != null)
            throw new ArgumentException(
                "--player and --attach-ipc-root are mutually exclusive."
            );
        if (player == null && launcher != null)
            throw new ArgumentException(
                "--launcher requires --player."
            );
        if (launcher != null && !File.Exists(launcher))
            throw new FileNotFoundException("Player launcher not found.", launcher);

        return new(
            server,
            player,
            launcher,
            launcherArguments.ToArray(),
            attachIpcRoot,
            expectScreenshotUnavailable
        );
    }
}
