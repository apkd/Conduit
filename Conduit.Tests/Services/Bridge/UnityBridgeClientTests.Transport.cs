using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    public async Task BridgeCommandSerializesToolUsageIntent()
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
        if (!OperatingSystem.IsWindows())
            return;

        var pipeName = $"unity-conduit-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        var waitForConnection = server.WaitForConnectionAsync();
        await using var transport = await BridgeTransport.ConnectAsync(
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
    public async Task FifoTransportReadHonorsCancellation()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-fifo-cancellation-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        await using var transport = await BridgeTransport.ConnectAsync(
            BridgeIdentifiers.GetPipeName(projectPath),
            TimeSpan.FromSeconds(2),
            CancellationToken.None
        );
        await transport.WritePayloadAsync(
            BridgeProtocol.Serialize(
                BridgeMessage.CreateHello(new() { ProjectPath = projectPath })
            ),
            CancellationToken.None
        );
        await Assert.That(await transport.ReadLineAsync(CancellationToken.None)).IsNotNull();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelled = false;
        try
        {
            await transport.ReadLineAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
    }

    [Test]
    public async Task ExecuteCommandIgnoresHandshakeProcessIdThatIsNotVisible()
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
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task ExecuteCommandReadsCoalescedUnixSocketResponses()
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
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
    }

    [Test]
    public async Task IdleRemoteCloseInvalidatesTheCachedHandshake()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-idle-close-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var result = await client.ExecuteCommandAsync(
            projectPath,
            BridgeIdentifiers.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        for (var attempt = 0; attempt < 100
             && client.TryGetLiveHandshake(projectPath, out _); attempt++)
            await Task.Delay(10);

        await Assert.That(client.TryGetLiveHandshake(projectPath, out _)).IsFalse();
    }

    [Test]
    public async Task StatusCompletesWhileAnotherCommandIsStillRunning()
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
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await bridge.CommandStarted.WaitAsync(TimeSpan.FromSeconds(10));
        var status = client.ExecuteCommandAsync(
            projectPath,
            "concurrent-status",
            new() { CommandType = BridgeCommandTypes.Status },
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None
        );

        await bridge.ConcurrentCommandCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        var statusResult = await status.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(statusResult.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(longCommand.IsCompleted).IsFalse();

        bridge.ReleaseFirstCommand();
        await Assert.That((await longCommand).Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
    }

    [Test]
    public async Task IdempotentCommandReconnectsOnceAfterDisconnect()
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
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );

        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(2);
    }
}
