using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    [Arguments(false, BridgeCommandTypes.CompilationReferences, false)]
    [Arguments(false, BridgeCommandTypes.AssemblyBlob, false)]
    [Arguments(true, BridgeCommandTypes.CompilationReferences, true)]
    [Arguments(true, BridgeCommandTypes.AssemblyBlob, true)]
    [Arguments(true, BridgeCommandTypes.Status, false)]
    [Arguments(false, BridgeCommandTypes.Status, false)]
    public async Task CompilationPhaseIsolationIsPlayerOnly(bool player, string command, bool isolated, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var path = $"/tmp/conduit-phase-isolation-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            path, int.MaxValue, endpointKind: player ? BridgeEndpointKinds.Player : BridgeEndpointKinds.Editor
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var first = await client.ExecuteCommandAsync(
            path, BridgeIdentifiers.CreateRequestId(), new() { CommandType = command }, TestTimeout, null, ct
        );
        await Assert.That(first.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(client.TryGetLiveHandshake(path, out _)).IsEqualTo(!isolated);

        var second = await client.ExecuteCommandAsync(
            path, BridgeIdentifiers.CreateRequestId(), new() { CommandType = BridgeCommandTypes.Status }, TestTimeout, null, ct
        );
        await Assert.That(second.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(isolated ? 2 : 1);
    }

    [Test]
    public async Task ReferenceRequestAndEditorCommandReuseOneConnection(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var path = $"/tmp/conduit-reference-reuse-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(path, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);
        var first = await compiler.GetReferencePathsAsync(path, ct);
        await Assert.That(first.Failure).IsNull();
        await Assert.That(client.TryGetLiveHandshake(path, out _)).IsTrue();
        var detours = first.ReferenceSet!.GetDetourCache();

        var status = await client.ExecuteCommandAsync(
            path,
            BridgeIdentifiers.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            TestTimeout,
            processIdHint: null,
            ct
        );
        var second = await compiler.GetReferencePathsAsync(path, ct);

        await Assert.That(status.Result?.Outcome).IsEqualTo(ToolOutcome.Success);
        await Assert.That(second.Failure).IsNull();
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
        await Assert.That(bridge.CommandCount).IsEqualTo(3);
        await Assert.That(ReferenceEquals(first.References, second.References)).IsTrue();
        await Assert.That(ReferenceEquals(detours, second.ReferenceSet!.GetDetourCache())).IsTrue();
    }

    [Test]
    public async Task ConcurrentProjectsShareStandardReferencesButNotDetourCaches(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        var firstPath = $"/tmp/conduit-sharing-a-{Guid.NewGuid():N}";
        var secondPath = $"/tmp/conduit-sharing-b-{Guid.NewGuid():N}";
        await using var firstBridge = await FakeFifoBridge.StartAsync(firstPath, int.MaxValue);
        await using var secondBridge = await FakeFifoBridge.StartAsync(secondPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var results = await Task.WhenAll(
            compiler.GetReferencePathsAsync(firstPath, ct),
            compiler.GetReferencePathsAsync(secondPath, ct)
        );

        await Assert.That(results.All(static value => value.Failure is null)).IsTrue();
        await Assert.That(ReferenceEquals(results[0].References![0], results[1].References![0])).IsTrue();
        await Assert.That(ReferenceEquals(results[0].ReferenceSet, results[1].ReferenceSet)).IsFalse();
        await Assert.That(ReferenceEquals(
            results[0].ReferenceSet!.GetDetourCache(),
            results[1].ReferenceSet!.GetDetourCache()
        )).IsFalse();
    }
}
