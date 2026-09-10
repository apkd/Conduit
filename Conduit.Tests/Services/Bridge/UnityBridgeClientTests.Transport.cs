using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    public async Task BridgeCommandSerializesToolUsageIntent(CancellationToken ct)
    {
        var payload = BridgeProtocol.Serialize(
            BridgeMessage.CreateCommand(
                "usage-test",
                new()
                {
                    CommandType = BridgeCommandTypes.Show,
                    TrackUsage = true,
                }
            )
        );
        var command = JsonNode.Parse(payload)?["command"];

        await Assert.That(command?["track_usage"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task ProbeTimeoutWhileWaitingForTheProjectGateReturnsATimeoutResult(CancellationToken ct)
    {
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var projectPath = $"/tmp/conduit-probe-timeout-{Guid.NewGuid():N}";
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var firstProbe = client.ProbeAsync(
            projectPath,
            processIdHint: null,
            timeout: TestTimeout,
            firstCancellation.Token
        );

        try
        {
            // the first probe holds the gate until cancellation; only the second probe times out
            var secondProbe = await client.ProbeAsync(
                projectPath,
                processIdHint: null,
                timeout: TimeSpan.FromMilliseconds(50),
                ct
            );

            await Assert.That(secondProbe.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ConnectTimedOut);
            await Assert.That(secondProbe.FailureDiagnostic).Contains("Could not establish a Unity connection");
            await Assert.That(secondProbe.Result).IsNull();
            await Assert.That(firstProbe.IsCompleted).IsFalse();
        }
        finally
        {
            firstCancellation.Cancel();
            try { await firstProbe; }
            catch (OperationCanceledException) when (firstCancellation.IsCancellationRequested) { }
        }
    }

    [Test]
    public async Task ProbeTreatsProcessIdHintAsAHintNotAFatalLivenessCheck(CancellationToken ct)
    {
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var projectPath = $"/tmp/conduit-stale-pid-{Guid.NewGuid():N}";

        var result = await client.ProbeAsync(
            projectPath,
            processIdHint: int.MaxValue,
            timeout: TimeSpan.FromMilliseconds(50),
            ct
        );

        await Assert.That(result.FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ConnectTimedOut);
        await Assert.That(result.FailureDiagnostic).Contains("Could not establish a Unity connection");
        await Assert.That(result.FailureDiagnostic).DoesNotContain("exited");
    }

    [Test]
    public async Task BridgeTransportConnectsToDotNetNamedPipeServer(CancellationToken ct)
    {
        // bound both sides while allowing delayed I/O completions on shared CI runners
        var timeout = TestTimeout;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pipeName = $"unity-conduit-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        var waitForConnection = server.WaitForConnectionAsync(cancellation.Token);
        await using var transport = await BridgeTransport.ConnectAsync(
            new BridgeEndpointDescriptor { Transport = BridgeTransportKinds.NamedPipe, PipeName = pipeName },
            timeout,
            cancellation.Token
        );

        await waitForConnection;

        await Assert.That(server.IsConnected).IsTrue();
        await Assert.That(transport.IsConnected).IsTrue();

        await transport.DisposeAsync();
        await Assert.That(transport.IsConnected).IsFalse();
    }

    [Test]
    public async Task FifoTransportReadHonorsCancellation(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-fifo-cancellation-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        await using var transport = await BridgeTransport.ConnectAsync(
            BridgeIdentifiers.GetPipeName(projectPath),
            TestTimeout,
            ct
        );
        await transport.WritePayloadAsync(
            BridgeProtocol.Serialize(
                BridgeMessage.CreateHello(new() { ProjectPath = projectPath })
            ),
            ct
        );
        await Assert.That(await transport.ReadLineAsync(ct)).IsNotNull();

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = transport.ReadLineAsync(cancellation.Token);
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await read;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
    }

    [Test]
    public async Task ExecuteCommandIgnoresHandshakeProcessIdThatIsNotVisible(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-invisible-pid-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ExecuteCommandAsync(
            projectPath,
            BridgeIdentifiers.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TestTimeout,
            processIdHint: null,
            ct
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task ExecuteCommandReadsCoalescedFifoResponses(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-coalesced-response-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            coalesceCommandResponses: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);

        var result = await client.ExecuteCommandAsync(
            projectPath,
            BridgeIdentifiers.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TestTimeout,
            processIdHint: null,
            ct
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task IdleRemoteCloseInvalidatesTheCachedHandshake(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-idle-close-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue, holdConnection: true);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var result = await client.ExecuteCommandAsync(
            projectPath,
            BridgeIdentifiers.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TestTimeout,
            processIdHint: null,
            ct
        );

        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(client.TryGetLiveHandshake(projectPath, out _)).IsTrue();
        bridge.CloseConnection();
        while (client.TryGetLiveHandshake(projectPath, out _))
            await Task.Delay(10, ct);

        await Assert.That(client.TryGetLiveHandshake(projectPath, out _)).IsFalse();
    }

    [Test]
    public async Task StatusCompletesWhileAnotherCommandIsStillRunning(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-multiplex-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            multiplexCommands: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var longCommand = client.ExecuteCommandAsync(
            projectPath,
            "long-command",
            new() { CommandType = BridgeCommandTypes.RunTestsEditMode },
            TestTimeout,
            processIdHint: null,
            ct
        );

        await bridge.CommandStarted.WaitAsync(ct);
        var status = client.ExecuteCommandAsync(
            projectPath,
            "concurrent-status",
            new() { CommandType = BridgeCommandTypes.Status },
            TestTimeout,
            processIdHint: null,
            ct
        );

        await bridge.ConcurrentCommandCompleted.WaitAsync(ct);
        var statusResult = await status.WaitAsync(ct);
        await Assert.That(statusResult.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(longCommand.IsCompleted).IsFalse();

        bridge.ReleaseFirstCommand();
        await Assert.That((await longCommand).Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
    }

    [Test]
    public async Task IdempotentCommandReconnectsOnceAfterDisconnect(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-idempotent-retry-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            disconnectFirstCommand: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var result = await client.ExecuteIdempotentCommandAsync(
            projectPath,
            "idempotent-read",
            new() { CommandType = BridgeCommandTypes.CompilationReferences },
            TestTimeout,
            ct
        );

        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(2);
    }
}
