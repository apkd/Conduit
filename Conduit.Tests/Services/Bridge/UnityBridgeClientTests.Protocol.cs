using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    public async Task ProcessExitSupersedesAConcurrentResultDisconnect(CancellationToken ct)
    {
        var disconnected = BridgeClientResult.Failure(
            new(),
            BridgeRuntimeFailureKind.ResultDisconnected,
            "disconnected",
            commandSent: true
        );
        var processExited = BridgeClientResult.Failure(
            new(),
            BridgeRuntimeFailureKind.ProcessExited,
            "process exited",
            commandSent: true
        );
        var processExit = new TaskCompletionSource<BridgeClientResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var resolution = UnityBridgeClient.PreferProcessExitAsync(
            disconnected,
            processExit.Task,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider()
        );
        await Assert.That(resolution.IsCompleted).IsFalse();
        processExit.SetResult(processExited);

        await Assert.That((await resolution).FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ProcessExited);
    }

    [Test]
    public async Task ResultDisconnectSurvivesWhenProcessExitIsNotConfirmed(CancellationToken ct)
    {
        var disconnected = BridgeClientResult.Failure(
            new(),
            BridgeRuntimeFailureKind.ResultDisconnected,
            "disconnected",
            commandSent: true
        );
        var processExit = new TaskCompletionSource<BridgeClientResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var resolution = UnityBridgeClient.PreferProcessExitAsync(disconnected, processExit.Task, clock);
        await Assert.That(resolution.IsCompleted).IsFalse();

        clock.Advance(TimeSpan.FromMinutes(1));

        await Assert.That((await resolution).FailureKind).IsEqualTo(BridgeRuntimeFailureKind.ResultDisconnected);
        await Assert.That(processExit.Task.IsCompleted).IsFalse();
    }

    [Test]
    public async Task OlderUnityProtocolReturnsATerminalCompatibilityDiagnostic(CancellationToken ct) =>
        await AssertProtocolMismatchAsync(
            BridgeProtocol.Version - 1,
            $"Unity Editor bridge protocol {BridgeProtocol.Version - 1} is older than Conduit server protocol {BridgeProtocol.Version}.",
            ct
        );

    [Test]
    public async Task NewerUnityProtocolReturnsATerminalCompatibilityDiagnostic(CancellationToken ct) =>
        await AssertProtocolMismatchAsync(
            BridgeProtocol.Version + 1,
            $"Conduit server protocol {BridgeProtocol.Version} is older than Unity Editor bridge protocol {BridgeProtocol.Version + 1}.",
            ct
        );

    [Test]
    public async Task CancellingATestRequestSendsCancellationAndWaitsForUnityToFinish(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-cancel-test-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            waitForCancellation: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        using var cancellation = new CancellationTokenSource();

        var execution = client.ExecuteCommandAsync(
            projectPath,
            "cancel-test-request",
            new() { CommandType = BridgeCommandTypes.RunTestsEditMode },
            TestTimeout,
            processIdHint: null,
            ct,
            cancellation.Token
        );

        await bridge.CommandStarted.WaitAsync(ct);
        cancellation.Cancel();
        var result = await execution.WaitAsync(ct);

        await Assert.That(
            await bridge.CancelledRequestId.WaitAsync(ct)
        ).IsEqualTo("cancel-test-request");
        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Cancelled);
    }

    [Test]
    public async Task ReceivePumpRoutesCommandStartedWhenTransportLivenessProbeIsFalse(CancellationToken ct)
    {
        var requestId = BridgeIdentifiers.CreateRequestId();
        var payload = BridgeProtocol.Serialize(BridgeMessage.CreateCommandStarted(requestId));
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        await using var transport = new BridgeTransport(
            async ct =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                    return await read.Task.WaitAsync(ct);

                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            },
            static (_, _) => Task.CompletedTask,
            static () => false,
            static () => ValueTask.CompletedTask
        );
        await using var connection = new BridgeClientConnection(
            transport,
            new()
            {
                ProjectPath = "/tmp/conduit-readable-disconnected",
                EditorProcessId = 0,
                SessionInstanceId = "test",
            },
            NullLogger<UnityBridgeClient>.Instance
        );
        var pending = connection.RegisterRequest(requestId, BridgeCommandTypes.Status);
        pending.MarkSent();
        read.SetResult(payload);

        var outcome = await connection.WaitForCommandStartedAsync(
            pending,
            ct,
            ct
        );

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.FinalResult).IsNull();
    }
}
