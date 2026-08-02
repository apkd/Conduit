using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

if (args.Length != 3
    || args[0] != "--transport"
    || args[1] is not ("stdio" or "http"))
    throw new ArgumentException(
        "Usage: dotnet run --file run-conduit-binary-smoke.cs -- " +
        "--transport <stdio|http> <conduit-executable>"
    );

var transport = args[1];
var conduitExecutable = Path.GetFullPath(args[2]);
var projectPath = Path.Combine(Path.GetTempPath(), $"conduit-smoke-{Environment.ProcessId}");
var previousIpcRoot = Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT");
Environment.SetEnvironmentVariable(
    "CONDUIT_IPC_ROOT",
    Path.Combine(projectPath, "ipc")
);

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
    var statusText = transport == "stdio"
        ? await RunStdioStatusAsync(conduitExecutable, projectPath)
        : await RunHttpStatusAsync(conduitExecutable, projectPath);
    if (!statusText.Contains("Unity 6000.3.10f1", StringComparison.Ordinal))
        throw new InvalidOperationException($"Binary smoke failed. Unexpected status response:\n{statusText}");

    Console.WriteLine($"Binary {transport} smoke passed.");
}
finally
{
    Environment.SetEnvironmentVariable("CONDUIT_IPC_ROOT", previousIpcRoot);
    try
    {
        if (Directory.Exists(projectPath))
            Directory.Delete(projectPath, recursive: true);
    }
    catch { }
}

static async Task<string> RunStdioStatusAsync(
    string conduitExecutable,
    string projectPath
)
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
        await RequestStdioAsync(process, ++requestId, "initialize", new JsonObject
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

        await RequestStdioAsync(
            process,
            ++requestId,
            "tools/list",
            new JsonObject(),
            TimeSpan.FromSeconds(10)
        );
        var status = await RequestStdioAsync(process, ++requestId, "tools/call", new JsonObject
        {
            ["name"] = "status",
            ["arguments"] = new JsonObject { ["projectPath"] = projectPath },
        }, TimeSpan.FromSeconds(20));

        return ReadStatusText(status);
    }
    catch
    {
        var stderr = await ReadCapturedStandardErrorAsync(stderrTask);
        if (process.HasExited)
            Console.Error.WriteLine($"Server exit code: {process.ExitCode}");

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

static async Task<string> RunHttpStatusAsync(
    string conduitExecutable,
    string projectPath
)
{
    var port = ReserveLoopbackPort();
    using var process = new Process
    {
        StartInfo = new(conduitExecutable)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        },
    };
    process.StartInfo.ArgumentList.Add("--http");
    process.StartInfo.ArgumentList.Add("--port");
    process.StartInfo.ArgumentList.Add(port.ToString());

    process.Start();
    var stderrTask = CaptureStandardErrorAsync(process);

    try
    {
        await WaitForHttpServerAsync(process, port, TimeSpan.FromSeconds(10));
        using var client = new HttpClient(
            new SocketsHttpHandler { UseProxy = false }
        )
        {
            BaseAddress = new($"http://127.0.0.1:{port}/"),
        };

        var requestId = 0;
        await RequestHttpAsync(client, ++requestId, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "conduit-binary-smoke",
                ["version"] = "0",
            },
        }, TimeSpan.FromSeconds(10));

        await SendHttpNotificationAsync(
            client,
            "notifications/initialized",
            new JsonObject(),
            TimeSpan.FromSeconds(10)
        );
        await RequestHttpAsync(
            client,
            ++requestId,
            "tools/list",
            new JsonObject(),
            TimeSpan.FromSeconds(10)
        );
        var status = await RequestHttpAsync(client, ++requestId, "tools/call", new JsonObject
        {
            ["name"] = "status",
            ["arguments"] = new JsonObject { ["projectPath"] = projectPath },
        }, TimeSpan.FromSeconds(20));

        return ReadStatusText(status);
    }
    catch
    {
        StopProcess(process);
        var stderr = await ReadCapturedStandardErrorAsync(stderrTask);
        if (process.HasExited)
            Console.Error.WriteLine($"Server exit code: {process.ExitCode}");

        if (!string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr);

        throw;
    }
    finally
    {
        StopProcess(process);
    }
}

static string ReadStatusText(JsonObject status)
    => status["result"]?["content"]?[0]?["text"]?.GetValue<string>()
       ?? throw new InvalidOperationException(
           $"Status response did not contain text content: {status}"
       );

static int ReserveLoopbackPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try
    {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task WaitForHttpServerAsync(
    Process process,
    int port,
    TimeSpan timeout
)
{
    using var cts = new CancellationTokenSource(timeout);
    while (true)
    {
        if (process.HasExited)
            throw new InvalidOperationException(
                $"The HTTP server exited during startup with code {process.ExitCode}."
            );

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return;
        }
        catch (SocketException) when (!cts.IsCancellationRequested)
        {
            await Task.Delay(50, cts.Token);
        }
    }
}

static async Task<JsonObject> RequestHttpAsync(
    HttpClient client,
    int requestId,
    string method,
    JsonObject parameters,
    TimeSpan timeout
)
{
    var payload = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = requestId,
        ["method"] = method,
        ["params"] = parameters,
    };
    using var response = await SendHttpAsync(client, payload, timeout);
    var body = await response.Content.ReadAsStringAsync();
    var result = ParseHttpResponse(
        response.Content.Headers.ContentType?.MediaType,
        body,
        requestId
    );
    if (result["error"] is not null)
        throw new InvalidOperationException(
            $"HTTP MCP request {requestId} returned an error: {result}"
        );

    return result;
}

static async Task SendHttpNotificationAsync(
    HttpClient client,
    string method,
    JsonObject parameters,
    TimeSpan timeout
)
{
    var payload = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = method,
        ["params"] = parameters,
    };
    using var response = await SendHttpAsync(client, payload, timeout);
}

static async Task<HttpResponseMessage> SendHttpAsync(
    HttpClient client,
    JsonObject payload,
    TimeSpan timeout
)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "")
    {
        Content = new StringContent(
            payload.ToJsonString(new() { WriteIndented = false }),
            Encoding.UTF8,
            "application/json"
        ),
    };
    request.Headers.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json")
    );
    request.Headers.Accept.Add(
        new MediaTypeWithQualityHeaderValue("text/event-stream")
    );

    using var cts = new CancellationTokenSource(timeout);
    var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cts.Token
    );
    try
    {
        response.EnsureSuccessStatusCode();
        if (response.Headers.Contains("Mcp-Session-Id"))
            throw new InvalidOperationException(
                "The stateless HTTP server returned an MCP session ID."
            );

        return response;
    }
    catch
    {
        response.Dispose();
        throw;
    }
}

static JsonObject ParseHttpResponse(
    string? mediaType,
    string body,
    int requestId
)
{
    if (mediaType == "text/event-stream")
    {
        foreach (var line in body.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (!value.StartsWith("data:", StringComparison.Ordinal))
                continue;

            if (JsonNode.Parse(value[5..].TrimStart()) is JsonObject candidate
                && candidate["id"]?.GetValue<int>() == requestId)
                return candidate;
        }

        throw new InvalidOperationException(
            $"SSE response did not contain JSON-RPC response {requestId}: {body}"
        );
    }

    if (JsonNode.Parse(body) is JsonObject response
        && response["id"]?.GetValue<int>() == requestId)
        return response;

    throw new InvalidOperationException(
        $"HTTP response did not contain JSON-RPC response {requestId}: {body}"
    );
}

static void StopProcess(Process process)
{
    if (process.HasExited)
        return;

    try
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }
    catch { }
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
        return string.Join(Environment.NewLine, lines.TakeLast(200));
    }
    catch
    {
        return string.Empty;
    }
}

static async Task<JsonObject> RequestStdioAsync(
    Process process,
    int requestId,
    string method,
    JsonObject parameters,
    TimeSpan timeout
)
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
        {
            var exitCode = process.HasExited ? process.ExitCode.ToString() : "unknown";
            throw new InvalidOperationException($"Server exited before response {requestId}. Exit code: {exitCode}.");
        }

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
    readonly string? endpointDirectory;
    readonly Task task;

    FakeBridge(string projectPath, string? endpointDirectory)
    {
        this.projectPath = projectPath;
        this.endpointDirectory = endpointDirectory;
        task = Task.Run(
            OperatingSystem.IsWindows()
                ? RunNamedPipeAsync
                : RunFifoAsync
        );
    }

    public static Task<FakeBridge> StartAsync(string projectPath)
    {
        if (OperatingSystem.IsWindows())
            return Task.FromResult(new FakeBridge(projectPath, null));

        var root = Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT")
                   ?? throw new InvalidOperationException("The smoke IPC root was not configured.");
        var endpointDirectory = Path.Combine(
            root,
            "endpoints",
            "editor-" + PipeName(projectPath)
        );
        Directory.CreateDirectory(Path.Combine(endpointDirectory, "clients"));
        return Task.FromResult(new FakeBridge(projectPath, endpointDirectory));
    }

    async Task RunNamedPipeAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    PipeName(projectPath),
                    PipeDirection.InOut,
                    16,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );
                await pipe.WaitForConnectionAsync(cts.Token);
                _ = Task.Run(() => HandleNamedPipeAsync(pipe));
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    }

    async Task HandleNamedPipeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
            await HandleAsync(pipe, pipe);
    }

    async Task RunFifoAsync()
    {
        var clientsDirectory = Path.Combine(endpointDirectory!, "clients");
        try
        {
            while (!cts.IsCancellationRequested)
            {
                foreach (var clientDirectory in Directory.GetDirectories(clientsDirectory))
                {
                    var requestPath = Path.Combine(clientDirectory, "request.json");
                    if (!File.Exists(requestPath))
                        continue;

                    try
                    {
                        File.Move(
                            requestPath,
                            Path.Combine(clientDirectory, "accepted.json")
                        );
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    _ = Task.Run(() => HandleFifoAsync(clientDirectory));
                }

                await Task.Delay(10, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    }

    async Task HandleFifoAsync(string clientDirectory)
    {
        await using var input = new FileStream(
            Path.Combine(clientDirectory, "to-unity.fifo"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous
        );
        await using var output = new FileStream(
            Path.Combine(clientDirectory, "from-unity.fifo"),
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous
        );
        await File.WriteAllTextAsync(
            Path.Combine(clientDirectory, "connected"),
            string.Empty,
            cts.Token
        );
        await HandleAsync(input, output);
    }

    async Task HandleAsync(Stream input, Stream output)
    {
        try
        {
            using var reader = new StreamReader(
                input,
                Utf8NoBom,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true
            );

            var serverHello = await reader.ReadLineAsync(cts.Token)
                              ?? throw new IOException(
                                  "Conduit closed before sending its handshake."
                              );
            var protocolVersion = JsonNode.Parse(serverHello)?["protocol_version"]
                                      ?.GetValue<int>()
                                  ?? throw new InvalidOperationException(
                                      $"Conduit handshake did not contain a protocol version: {serverHello}"
                                  );
            await WriteAsync(output, new JsonObject
            {
                ["protocol_version"] = protocolVersion,
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

            await WriteAsync(output, new JsonObject
            {
                ["protocol_version"] = protocolVersion,
                ["message_type"] = "command_started",
                ["request_id"] = requestId,
            });
            await WriteAsync(output, new JsonObject
            {
                ["protocol_version"] = protocolVersion,
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
        try { await task.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        try
        {
            if (endpointDirectory != null)
                Directory.Delete(endpointDirectory, recursive: true);
        }
        catch { }
        cts.Dispose();
    }

    static string PipeName(string projectPath)
    {
        // keep the standalone fake bridge aligned with the endpoint identity implemented by the binary under test
        const string prefix = "unity-conduit-";
        const int legacySlugMaxLength = 50;
        const int slugMaxLength = 32;
        const ulong hashOffset = 14695981039346656037UL;
        const ulong hashPrime = 1099511628211UL;

        var normalizedPath = NormalizeProjectPath(projectPath);
        var slug = CreateSlug(normalizedPath, legacySlugMaxLength + 1);
        if (slug.Length is > 0 and <= legacySlugMaxLength)
            return prefix + slug;

        if (slug.Length > slugMaxLength)
            slug = slug[..slugMaxLength].TrimEnd('_');

        var hash = hashOffset;
        foreach (var character in normalizedPath)
        {
            hash ^= ToLowerAscii(character);
            hash *= hashPrime;
        }

        return slug.Length == 0
            ? $"{prefix}{hash:x16}"
            : $"{prefix}{slug}-{hash:x16}";

        static string NormalizeProjectPath(string path)
        {
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            if (normalized.Length < 2 || normalized[1] != ':' || !char.IsAsciiLetter(normalized[0]))
                return normalized;

            var remainder = normalized.AsSpan(2).TrimStart('/');
            if (remainder.StartsWith("mnt", StringComparison.OrdinalIgnoreCase)
                && (remainder.Length == 3 || remainder[3] == '/'))
                return "/" + remainder.ToString();

            return remainder.IsEmpty
                ? $"/mnt/{char.ToLowerInvariant(normalized[0])}"
                : $"/mnt/{char.ToLowerInvariant(normalized[0])}/{remainder}";
        }

        static string CreateSlug(string normalizedPath, int maxLength)
        {
            var builder = new StringBuilder(Math.Min(normalizedPath.Length, maxLength));
            var previousWasSeparator = false;
            foreach (var character in normalizedPath)
            {
                if (builder.Length >= maxLength)
                    break;

                if (char.IsAsciiLetterOrDigit(character))
                {
                    builder.Append(ToLowerAscii(character));
                    previousWasSeparator = false;
                }
                else if (!previousWasSeparator && builder.Length > 0)
                {
                    builder.Append('_');
                    previousWasSeparator = true;
                }
            }

            if (builder.Length > 0 && builder[^1] == '_')
                builder.Length--;

            return builder.ToString();
        }

        static char ToLowerAscii(char character) =>
            character is >= 'A' and <= 'Z'
                ? (char)(character + ('a' - 'A'))
                : character;
    }
}
