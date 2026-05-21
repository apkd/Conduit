using JetBrains.Annotations;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class UnityBridgeClientTests
{
    [Test]
    public async Task ProbeTimeoutWhileWaitingForTheProjectGateReturnsATimeoutResult()
    {
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var projectPath = $"/tmp/conduit-probe-timeout-{Guid.NewGuid():N}";

        var firstProbe = client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TimeSpan.FromMilliseconds(900),
            CancellationToken.None
        );

        await Task.Delay(50);

        var secondProbe = await client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TimeSpan.FromMilliseconds(50),
            CancellationToken.None
        );

        await Assert.That(secondProbe.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ConnectTimedOut);
        await Assert.That(secondProbe.FailureDiagnostic).Contains("Could not establish a Unity connection");
        await Assert.That(secondProbe.Result).IsNull();

        await firstProbe;
    }

    [Test]
    public async Task ProbeTreatsProcessIdHintAsAHintNotAFatalLivenessCheck()
    {
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var projectPath = $"/tmp/conduit-stale-pid-{Guid.NewGuid():N}";

        var result = await client.ProbeAsync(
            projectPath,
            processIdHint: int.MaxValue,
            timeout: TimeSpan.FromMilliseconds(50),
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ConnectTimedOut);
        await Assert.That(result.FailureDiagnostic).Contains("Could not establish a Unity connection");
        await Assert.That(result.FailureDiagnostic).DoesNotContain("exited");
    }

    [Test]
    public async Task BridgeTransportConnectsToDotNetNamedPipeServer()
    {
        var pipeName = $"unity-conduit-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        var waitForConnection = server.WaitForConnectionAsync();
        await using var transport = await UnityBridgeClient.BridgeTransport.ConnectAsync(
            pipeName,
            TimeSpan.FromSeconds(2),
            CancellationToken.None
        );

        await waitForConnection.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.That(server.IsConnected).IsTrue();
        await Assert.That(transport.IsConnected).IsTrue();

        await transport.DisposeAsync();
        await Assert.That(transport.IsConnected).IsFalse();
    }

    [Test]
    public async Task ExecuteCommandIgnoresHandshakeProcessIdThatIsNotVisible()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-invisible-pid-{Guid.NewGuid():N}";
        await using var bridge = await FakeUnixBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ExecuteCommandAsync(
            projectPath,
            ConduitUtility.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(2),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task CommandStartWaitReadsPayloadWhenTransportLivenessProbeIsFalse()
    {
        var requestId = ConduitUtility.CreateRequestId();
        var payload = BridgeProtocol.Serialize(BridgeMessage.CreateCommandStarted(requestId));
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload + "\n"));
        await using var transport = new UnityBridgeClient.BridgeTransport(stream, static () => false, static () => ValueTask.CompletedTask);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var connection = new UnityBridgeClient.BridgeClientConnection(
            transport,
            reader,
            new()
            {
                ProjectPath = "/tmp/conduit-readable-disconnected",
                EditorProcessId = 0,
                SessionInstanceId = "test",
            },
            NullLogger<UnityBridgeClient>.Instance
        );

        var outcome = await connection.WaitForCommandStartedAsync(
            requestId,
            BridgeCommandTypes.Status,
            commandSent: true,
            TimeSpan.FromSeconds(1),
            CancellationToken.None,
            CancellationToken.None
        );

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.FinalResult).IsNull();
    }

    sealed class FakeUnixBridge : IAsyncDisposable
    {
        static readonly UTF8Encoding Utf8NoBom = new(false);
        static readonly byte[] Newline = [(byte)'\n'];
        readonly CancellationTokenSource cts = new();
        readonly string projectPath;
        readonly int editorProcessId;
        readonly string socketPath;
        readonly Socket listener;
        readonly Task serverTask;

        FakeUnixBridge(string projectPath, int editorProcessId, string socketPath, Socket listener)
        {
            this.projectPath = projectPath;
            this.editorProcessId = editorProcessId;
            this.socketPath = socketPath;
            this.listener = listener;
            serverTask = Task.Run(RunAsync);
        }

        public static Task<FakeUnixBridge> StartAsync(string projectPath, int editorProcessId)
        {
            var socketPath = UnityBridgeClient.BridgeTransport.GetDotNetUnixPipePath(ConduitUtility.GetPipeName(projectPath));
            try
            {
                File.Delete(socketPath);
            }
            catch { }

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(16);
            return Task.FromResult(new FakeUnixBridge(projectPath, editorProcessId, socketPath, listener));
        }

        async Task RunAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var socket = await listener.AcceptAsync(cts.Token);
                    _ = Task.Run(() => HandleClientAsync(socket));
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        }

        async Task HandleClientAsync(Socket socket)
        {
            await using var stream = new NetworkStream(socket, ownsSocket: true);
            using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await reader.ReadLineAsync(cts.Token);
            await WritePayloadAsync(stream, new JsonObject
            {
                ["protocol_version"] = 2,
                ["message_type"] = "hello",
                ["project"] = new JsonObject
                {
                    ["project_path"] = projectPath,
                    ["display_name"] = Path.GetFileName(projectPath),
                    ["unity_version"] = "6000.3.10f1",
                    ["editor_process_id"] = editorProcessId,
                    ["session_instance_id"] = "fake-bridge",
                    ["last_seen_utc"] = DateTimeOffset.UtcNow,
                },
            }, cts.Token);

            var commandPayload = await reader.ReadLineAsync(cts.Token);
            var requestId = JsonNode.Parse(commandPayload!)?["request_id"]?.GetValue<string>() ?? "";
            await WritePayloadAsync(stream, new JsonObject
            {
                ["protocol_version"] = 2,
                ["message_type"] = "command_started",
                ["request_id"] = requestId,
            }, cts.Token);
            await WritePayloadAsync(stream, new JsonObject
            {
                ["protocol_version"] = 2,
                ["message_type"] = "command_result",
                ["request_id"] = requestId,
                ["result"] = new JsonObject
                {
                    ["outcome"] = ToolOutcome.Success,
                    ["logs"] = "",
                    ["return_value"] = "",
                },
            }, cts.Token);
        }

        static async Task WritePayloadAsync(Stream stream, JsonObject payload, CancellationToken ct)
        {
            await stream.WriteAsync(Utf8NoBom.GetBytes(payload.ToJsonString(new() { WriteIndented = false })), ct);
            await stream.WriteAsync(Newline, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Dispose();
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch { }

            try
            {
                File.Delete(socketPath);
            }
            catch { }

            cts.Dispose();
        }
    }
}
