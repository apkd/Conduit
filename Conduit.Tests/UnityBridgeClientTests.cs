using JetBrains.Annotations;
using Microsoft.Extensions.Logging.Abstractions;

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
}
