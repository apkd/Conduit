using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

[Timeout(120_000)]
public sealed partial class UnityBridgeClientTests
{
    static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(1);

    static async Task AssertProtocolMismatchAsync(int unityProtocolVersion, string expectedDiagnostic, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-protocol-mismatch-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            handshakeProtocolVersion: unityProtocolVersion
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TestTimeout,
            ct: ct
        );

        await Assert.That(result.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ProtocolMismatch);
        await Assert.That(result.FailureDiagnostic).IsEqualTo(expectedDiagnostic);
        await Assert.That(result.Handshake).IsNull();
        await Assert.That(result.CommandSent).IsFalse();
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
    }

    sealed class FakeFifoBridge : IAsyncDisposable
    {
        static readonly UTF8Encoding Utf8NoBom = new(false);
        static readonly byte[] Newline = [(byte)'\n'];
        readonly CancellationTokenSource cts = new();
        readonly string projectPath;
        readonly int editorProcessId;
        readonly int handshakeProtocolVersion;
        readonly bool coalesceCommandResponses;
        readonly bool waitForCancellation;
        readonly bool multiplexCommands;
        readonly bool disconnectFirstCommand;
        readonly bool changeSessionOnReconnect;
        readonly bool preserveSnippetsAfterFirstCommand;
        readonly bool holdCommand;
        readonly bool holdConnection;
        readonly string endpointDirectory;
        readonly Task serverTask;
        readonly TaskCompletionSource commandStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<string?> cancelledRequestId = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource concurrentCommandCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource releaseFirstCommand = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource closeConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int connectionCount;
        int commandCount;

        FakeFifoBridge(
            string projectPath,
            int editorProcessId,
            int handshakeProtocolVersion,
            bool coalesceCommandResponses,
            bool waitForCancellation,
            bool multiplexCommands,
            bool disconnectFirstCommand,
            bool changeSessionOnReconnect,
            bool preserveSnippetsAfterFirstCommand,
            bool holdCommand,
            bool holdConnection,
            string endpointDirectory)
        {
            this.projectPath = projectPath;
            this.editorProcessId = editorProcessId;
            this.handshakeProtocolVersion = handshakeProtocolVersion;
            this.coalesceCommandResponses = coalesceCommandResponses;
            this.waitForCancellation = waitForCancellation;
            this.multiplexCommands = multiplexCommands;
            this.disconnectFirstCommand = disconnectFirstCommand;
            this.changeSessionOnReconnect = changeSessionOnReconnect;
            this.preserveSnippetsAfterFirstCommand = preserveSnippetsAfterFirstCommand;
            this.holdCommand = holdCommand;
            this.holdConnection = holdConnection;
            this.endpointDirectory = endpointDirectory;
            serverTask = RunAsync();
        }

        public Task CommandStarted => commandStarted.Task;

        public Task<string?> CancelledRequestId => cancelledRequestId.Task;

        public Task ConcurrentCommandCompleted => concurrentCommandCompleted.Task;

        public int ConnectionCount => Volatile.Read(ref connectionCount);
        public int CommandCount => Volatile.Read(ref commandCount);

        public void ReleaseFirstCommand() => releaseFirstCommand.TrySetResult();
        public void CloseConnection() => closeConnection.TrySetResult();

        public static Task<FakeFifoBridge> StartAsync(
            string projectPath,
            int editorProcessId,
            bool coalesceCommandResponses = false,
            bool waitForCancellation = false,
            bool multiplexCommands = false,
            bool disconnectFirstCommand = false,
            bool changeSessionOnReconnect = false,
            bool preserveSnippetsAfterFirstCommand = false,
            bool holdCommand = false,
            bool holdConnection = false,
            int handshakeProtocolVersion = BridgeProtocol.Version)
        {
            var endpointDirectory = ConduitIpcPaths.GetEndpointDirectory(
                ConduitIpcPaths.GetDiscoveryRoots()[0],
                "editor-" + BridgeIdentifiers.GetPipeName(projectPath)
            );
            try
            {
                Directory.Delete(endpointDirectory, recursive: true);
            }
            catch { }

            Directory.CreateDirectory(Path.Combine(endpointDirectory, "clients"));
            return Task.FromResult(
                new FakeFifoBridge(
                    projectPath,
                    editorProcessId,
                    handshakeProtocolVersion,
                    coalesceCommandResponses,
                    waitForCancellation,
                    multiplexCommands,
                    disconnectFirstCommand,
                    changeSessionOnReconnect,
                    preserveSnippetsAfterFirstCommand,
                    holdCommand,
                    holdConnection,
                    endpointDirectory
                )
            );
        }

        async Task RunAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var clientDirectory in Directory.GetDirectories(
                                 Path.Combine(endpointDirectory, "clients")
                             ))
                    {
                        var publicationPath = Path.Combine(clientDirectory, "request.json");
                        if (!File.Exists(publicationPath))
                            continue;

                        try
                        {
                            File.Move(
                                publicationPath,
                                Path.Combine(clientDirectory, "accepted.json")
                            );
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        await HandleClientAsync(clientDirectory);
                    }

                    await Task.Delay(10, cts.Token);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception exception) when (cts.IsCancellationRequested
                && exception is IOException or ObjectDisposedException) { }
        }

        async Task HandleClientAsync(string clientDirectory)
        {
            var handshakeReceived = false;
            try
            {
                using var reader = BridgeTransport.FifoLineReader.Open(Path.Combine(clientDirectory, "to-unity.fifo"));
                await using var output = OpenResponseWriter(Path.Combine(clientDirectory, "from-unity.fifo"));
                await File.WriteAllTextAsync(
                    Path.Combine(clientDirectory, "connected"),
                    string.Empty,
                    cts.Token
                );
                if (await reader.ReadLineAsync(cts.Token) is null)
                    return;

                handshakeReceived = true;
                var connectionNumber = Interlocked.Increment(ref connectionCount);
                await WritePayloadAsync(output, new JsonObject
                {
                    ["protocol_version"] = handshakeProtocolVersion,
                    ["message_type"] = "hello",
                    ["project"] = new JsonObject
                    {
                        ["project_path"] = projectPath,
                        ["display_name"] = Path.GetFileName(projectPath),
                        ["unity_version"] = "6000.3.10f1",
                        ["editor_process_id"] = editorProcessId,
                        ["session_instance_id"] = changeSessionOnReconnect
                            ? "fake-bridge-" + connectionNumber
                            : "fake-bridge",
                        ["preserve_snippets"] = false,
                        ["last_seen_utc"] = DateTimeOffset.UtcNow,
                    },
                }, cts.Token);

                if (handshakeProtocolVersion != BridgeProtocol.Version)
                    return;

                var commandPayload = await reader.ReadLineAsync(cts.Token);
                if (commandPayload is null)
                    return;

                var currentCommand = Interlocked.Increment(ref commandCount);
                if (disconnectFirstCommand && currentCommand == 1)
                    return;

                var commandJson = JsonNode.Parse(commandPayload);
                var requestId = commandJson?["request_id"]?.GetValue<string>() ?? "";
                var commandType = commandJson?["command"]?["command_type"]?.GetValue<string>();
                var commandStarted = new JsonObject
                {
                    ["protocol_version"] = BridgeProtocol.Version,
                    ["message_type"] = "command_started",
                    ["request_id"] = requestId,
                };

                var commandResult = new JsonObject
                {
                    ["protocol_version"] = BridgeProtocol.Version,
                    ["message_type"] = "command_result",
                    ["request_id"] = requestId,
                    ["result"] = new JsonObject
                    {
                        ["outcome"] = ToolOutcome.Success,
                        ["logs"] = "",
                        ["return_value"] = "",
                    },
                };
                if (commandType == BridgeCommandTypes.CompilationReferences)
                {
                    commandResult["result"]!["return_value"] = JsonSerializer.Serialize(
                        new BridgeAssemblyReferenceManifest
                        {
                            PreserveSnippets = preserveSnippetsAfterFirstCommand
                                               && currentCommand > 1,
                            References =
                            [
                                new()
                                {
                                    Id = Guid.NewGuid().ToString("N"),
                                    AssemblyName = typeof(object).Assembly.FullName ?? "System.Private.CoreLib",
                                    Path = typeof(object).Assembly.Location,
                                    Length = new FileInfo(typeof(object).Assembly.Location).Length,
                                },
                            ],
                        },
                        ConduitJsonContext.Default.BridgeAssemblyReferenceManifest
                    );
                }

                if (multiplexCommands)
                {
                    await WritePayloadAsync(output, commandStarted, cts.Token);
                    this.commandStarted.TrySetResult();
                    var concurrentPayload = await reader.ReadLineAsync(cts.Token);
                    if (concurrentPayload is null)
                        return;

                    var concurrentRequestId = JsonNode.Parse(concurrentPayload)?["request_id"]?.GetValue<string>() ?? "";
                    commandStarted["request_id"] = concurrentRequestId;
                    commandResult["request_id"] = concurrentRequestId;
                    await WritePayloadsAsync(output, cts.Token, commandStarted, commandResult);
                    concurrentCommandCompleted.TrySetResult();
                    await releaseFirstCommand.Task.WaitAsync(cts.Token);
                    commandResult["request_id"] = requestId;
                    await WritePayloadAsync(output, commandResult, cts.Token);
                    return;
                }

                if (waitForCancellation)
                {
                    await WritePayloadAsync(output, commandStarted, cts.Token);
                    this.commandStarted.TrySetResult();
                    var cancellationPayload = await reader.ReadLineAsync(cts.Token);
                    if (cancellationPayload is null)
                        return;

                    var cancellation = JsonNode.Parse(cancellationPayload);
                    cancelledRequestId.TrySetResult(
                        cancellation?["message_type"]?.GetValue<string>() == "cancel_command"
                            ? cancellation["request_id"]?.GetValue<string>()
                            : null
                    );
                    commandResult["result"]!["outcome"] = ToolOutcome.Cancelled;
                    await WritePayloadAsync(output, commandResult, cts.Token);
                }
                else if (coalesceCommandResponses)
                    await WritePayloadsAsync(output, cts.Token, commandStarted, commandResult);
                else
                {
                    await WritePayloadAsync(output, commandStarted, cts.Token);
                    this.commandStarted.TrySetResult();
                    if (holdCommand)
                        await releaseFirstCommand.Task.WaitAsync(cts.Token);
                    await WritePayloadAsync(output, commandResult, cts.Token);
                }

                if (holdConnection)
                    await closeConnection.Task.WaitAsync(cts.Token);
            }
            catch (IOException) when (!handshakeReceived)
            {
                // the client can abandon a connection attempt before the fixture accepts it
            }

            static FileStream OpenResponseWriter(string path)
            {
                // keep a reader open during setup so an abandoned client cannot block open(2)
                using var keeper = BridgeTransport.FifoLineReader.Open(path);
                return new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            }
        }

        static async Task WritePayloadAsync(Stream stream, JsonObject payload, CancellationToken ct)
            => await WritePayloadsAsync(stream, ct, payload);

        static async Task WritePayloadsAsync(Stream stream, CancellationToken ct, params JsonObject[] payloads)
        {
            foreach (var payload in payloads)
            {
                await stream.WriteAsync(Utf8NoBom.GetBytes(payload.ToJsonString(new() { WriteIndented = false })), ct);
                await stream.WriteAsync(Newline, ct);
            }

            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try
            {
                await serverTask.WaitAsync(TestTimeout);
            }
            finally
            {
                Directory.Delete(endpointDirectory, recursive: true);
                cts.Dispose();
            }
        }
    }
}
