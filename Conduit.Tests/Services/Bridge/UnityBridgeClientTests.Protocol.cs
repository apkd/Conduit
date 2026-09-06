using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    public async Task ProcessExitSupersedesAConcurrentResultDisconnect()
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
    public async Task ResultDisconnectSurvivesWhenProcessExitIsNotConfirmed()
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
    public async Task OlderUnityProtocolReturnsATerminalCompatibilityDiagnostic() =>
        await AssertProtocolMismatchAsync(
            BridgeProtocol.Version - 1,
            $"Unity Editor bridge protocol {BridgeProtocol.Version - 1} is older than Conduit server protocol {BridgeProtocol.Version}."
        );

    [Test]
    public async Task NewerUnityProtocolReturnsATerminalCompatibilityDiagnostic() =>
        await AssertProtocolMismatchAsync(
            BridgeProtocol.Version + 1,
            $"Conduit server protocol {BridgeProtocol.Version} is older than Unity Editor bridge protocol {BridgeProtocol.Version + 1}."
        );

    [Test]
    public async Task CancellingATestRequestSendsCancellationAndWaitsForUnityToFinish()
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
            TimeSpan.FromSeconds(10),
            processIdHint: null,
            CancellationToken.None,
            cancellation.Token
        );

        await bridge.CommandStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(15));

        await Assert.That(
            await bridge.CancelledRequestId.WaitAsync(TimeSpan.FromSeconds(15))
        ).IsEqualTo("cancel-test-request");
        await Assert.That(result.FailureKind).IsNull();
        await Assert.That(result.Result?.Outcome).IsEqualTo(ToolOutcome.Cancelled);
    }

    [Test]
    public async Task ReceivePumpRoutesCommandStartedWhenTransportLivenessProbeIsFalse()
    {
        var requestId = BridgeIdentifiers.CreateRequestId();
        var payload = BridgeProtocol.Serialize(BridgeMessage.CreateCommandStarted(requestId));
        var read = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        await using var transport = new BridgeTransport(
            async ct =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                    return await read.Task;

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
            CancellationToken.None,
            CancellationToken.None
        );

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.FinalResult).IsNull();
    }
}
