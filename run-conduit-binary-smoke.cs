using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 1)
    throw new ArgumentException("Usage: dotnet run --file run-conduit-binary-smoke.cs -- <conduit-executable>");

var conduitExecutable = Path.GetFullPath(args[0]);
var projectPath = Path.Combine(Path.GetTempPath(), $"conduit-smoke-{Environment.ProcessId}");

try
{
    Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
    Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
    await File.WriteAllTextAsync(Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.10f1\n");
    await File.WriteAllTextAsync(Path.Combine(projectPath, "Packages", "manifest.json"), """
    {
      "dependencies": {
        "dev.tryfinally.conduit": "https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release"
      }
    }
    """);

    await using var bridge = await FakeBridge.StartAsync(projectPath);
    var statusText = await RunStatusAsync(conduitExecutable, projectPath);
    if (!statusText.Contains("Unity 6000.3.10f1", StringComparison.Ordinal))
        throw new InvalidOperationException($"Binary smoke failed. Unexpected status response:\n{statusText}");

    Console.WriteLine("Binary smoke passed.");
}
finally
{
    try
    {
        if (Directory.Exists(projectPath))
            Directory.Delete(projectPath, recursive: true);
    }
    catch { }
}

static async Task<string> RunStatusAsync(string conduitExecutable, string projectPath)
{
    using var process = new Process
    {
        StartInfo = new(conduitExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        },
    };

    process.Start();
    var stderrTask = CaptureStandardErrorAsync(process);

    try
    {
        var requestId = 0;
        await RequestAsync(process, ++requestId, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "conduit-binary-smoke", ["version"] = "0" },
        }, TimeSpan.FromSeconds(10));

        await SendAsync(process, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
            ["params"] = new JsonObject(),
        });

        await RequestAsync(process, ++requestId, "tools/list", new JsonObject(), TimeSpan.FromSeconds(10));
        var status = await RequestAsync(process, ++requestId, "tools/call", new JsonObject
        {
            ["name"] = "status",
            ["arguments"] = new JsonObject { ["projectPath"] = projectPath },
        }, TimeSpan.FromSeconds(20));

        return status["result"]?["content"]?[0]?["text"]?.GetValue<string>()
               ?? throw new InvalidOperationException($"Status response did not contain text content: {status}");
    }
    catch
    {
        var stderr = await ReadCapturedStandardErrorAsync(stderrTask);
        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr);

        throw;
    }
    finally
    {
        try { process.StandardInput.Close(); } catch { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(cts.Token); } catch { try { process.Kill(); } catch { } }
    }
}

static async Task<List<string>> CaptureStandardErrorAsync(Process process)
{
    var lines = new List<string>();
    while (await process.StandardError.ReadLineAsync() is { } line)
        if (!string.IsNullOrWhiteSpace(line))
            lines.Add(line);

    return lines;
}

static async Task<string> ReadCapturedStandardErrorAsync(Task<List<string>> stderrTask)
{
    try
    {
        var lines = await stderrTask.WaitAsync(TimeSpan.FromSeconds(1));
        return string.Join(Environment.NewLine, lines.TakeLast(24));
    }
    catch
    {
        return string.Empty;
    }
}

static async Task<JsonObject> RequestAsync(Process process, int requestId, string method, JsonObject parameters, TimeSpan timeout)
{
    await SendAsync(process, new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = requestId,
        ["method"] = method,
        ["params"] = parameters,
    });

    using var cts = new CancellationTokenSource(timeout);
    while (true)
    {
        var line = await process.StandardOutput.ReadLineAsync(cts.Token);
        if (line is null)
            throw new InvalidOperationException($"Server exited before response {requestId}.");

        if (JsonNode.Parse(line) is JsonObject response && response["id"]?.GetValue<int>() == requestId)
            return response;
    }
}

static async Task SendAsync(Process process, JsonObject payload)
{
    await process.StandardInput.WriteLineAsync(payload.ToJsonString(new() { WriteIndented = false }));
    await process.StandardInput.FlushAsync();
}

sealed class FakeBridge : IAsyncDisposable
{
    static readonly UTF8Encoding Utf8NoBom = new(false);
    static readonly byte[] Newline = [(byte)'\n'];
    readonly CancellationTokenSource cts = new();
    readonly string projectPath;
    readonly string socketPath;
    readonly Socket listener;
    readonly Task task;

    FakeBridge(string projectPath, string socketPath, Socket listener)
    {
        this.projectPath = projectPath;
        this.socketPath = socketPath;
        this.listener = listener;
        task = Task.Run(RunAsync);
    }

    public static Task<FakeBridge> StartAsync(string projectPath)
    {
        var socketPath = Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName(projectPath));
        try { File.Delete(socketPath); } catch { }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(64);
        return Task.FromResult(new FakeBridge(projectPath, socketPath, listener));
    }

    async Task RunAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
                _ = Task.Run(() => HandleAsync(listener.AcceptAsync(cts.Token).AsTask()));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    }

    async Task HandleAsync(Task<Socket> socketTask)
    {
        try
        {
            using var socket = await socketTask;
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await reader.ReadLineAsync(cts.Token);
            await WriteAsync(stream, new JsonObject
            {
                ["protocol_version"] = 2,
                ["message_type"] = "hello",
                ["project"] = new JsonObject
                {
                    ["project_path"] = projectPath,
                    ["display_name"] = Path.GetFileName(projectPath),
                    ["unity_version"] = "6000.3.10f1",
                    ["editor_process_id"] = Environment.ProcessId,
                    ["session_instance_id"] = "fake-bridge",
                    ["last_seen_utc"] = DateTimeOffset.UtcNow,
                },
            });

            var command = await reader.ReadLineAsync(cts.Token)
                          ?? throw new IOException("Conduit closed before sending a command.");
            var requestId = JsonNode.Parse(command)?["request_id"]?.GetValue<string>() ?? "";

            await WriteAsync(stream, new JsonObject
            {
                ["protocol_version"] = 2,
                ["message_type"] = "command_started",
                ["request_id"] = requestId,
            });
            await WriteAsync(stream, new JsonObject
            {
                ["protocol_version"] = 2,
                ["message_type"] = "command_result",
                ["request_id"] = requestId,
                ["result"] = new JsonObject
                {
                    ["outcome"] = "success",
                    ["logs"] = "",
                    ["return_value"] = """
                    {"unity_version":"6000.3.10f1","platform":"StandaloneLinux64","editor_process_id":1,"uptime":"1 second","editor_log_path":"/tmp/conduit-binary-smoke/Logs/Editor.log","editor_mode":"edit mode","is_paused":false,"is_compiling":false,"is_updating":false,"active_command_type":"","scenes":[],"dirty_scenes":[]}
                    """,
                },
            });
        }
        catch (Exception exception) when (!cts.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Fake bridge client failed: {exception.Message}");
        }
    }

    static async Task WriteAsync(Stream stream, JsonObject payload)
    {
        await stream.WriteAsync(Utf8NoBom.GetBytes(payload.ToJsonString(new() { WriteIndented = false })));
        await stream.WriteAsync(Newline);
        await stream.FlushAsync();
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        listener.Dispose();
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        try { File.Delete(socketPath); } catch { }
        cts.Dispose();
    }

    static string PipeName(string projectPath)
    {
        var builder = new StringBuilder("unity-conduit-");
        var previousWasSeparator = false;
        foreach (var character in projectPath.Replace('\\', '/'))
        {
            if (builder.Length >= 64)
                break;

            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator && builder.Length > "unity-conduit-".Length)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        if (builder[^1] == '_')
            builder.Length--;

        return builder.ToString();
    }
}
