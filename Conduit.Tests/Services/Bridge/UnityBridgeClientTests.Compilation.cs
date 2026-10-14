using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    public async Task CompilationReferencesAreSingleFlightWithinAUnitySession(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-cache-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue, holdCommand: true);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var first = compiler.GetReferencePathsAsync(projectPath, ct);
        await bridge.CommandStarted.WaitAsync(ct);
        var second = compiler.GetReferencePathsAsync(projectPath, ct);
        // both callers must join the same in-flight request before the bridge can answer
        bridge.ReleaseFirstCommand();
        var results = await Task.WhenAll(first, second);

        await Assert.That(results.All(static result => result.Failure is null)).IsTrue();
        await Assert.That(bridge.CommandCount).IsEqualTo(1);
    }

    [Test]
    public async Task NewUnitySessionRefetchesCompilationReferences(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-session-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            changeSessionOnReconnect: true,
            holdConnection: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var first = await compiler.GetReferencePathsAsync(projectPath, ct);
        await Assert.That(first.Failure).IsNull();
        var oldDetour = first.ReferenceSet!.GetDetourCache();

        // model a real reload/peer close instead of relying on reference requests to close the connection.
        bridge.CloseConnection();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (client.TryGetLiveHandshake(projectPath, out _))
            await Task.Delay(10, timeout.Token);

        var second = await compiler.GetReferencePathsAsync(projectPath, ct);
        await Assert.That(second.Failure).IsNull();
        await Assert.That(second.SessionInstanceId != first.SessionInstanceId).IsTrue();
        await Assert.That(ReferenceEquals(first.ReferenceSet, second.ReferenceSet)).IsFalse();
        await Assert.That(ReferenceEquals(oldDetour, second.ReferenceSet!.GetDetourCache())).IsFalse();
        // a caller holding the old snapshot can still use it; replacement does not dispose it underneath work.
        await Assert.That(oldDetour.SessionInstanceId).IsEqualTo(first.SessionInstanceId);
        await Assert.That(bridge.ConnectionCount).IsEqualTo(2);
        await Assert.That(bridge.CommandCount).IsEqualTo(2);
    }

    [Test]
    public async Task CachedCompilationReferencesRefreshMutableMetadata(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-handshake-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            preserveSnippetsAfterFirstCommand: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var first = await compiler.GetReferencePathsAsync(projectPath, ct);
        await Assert.That(first.Failure).IsNull();
        await Assert.That(first.PreserveSnippets).IsFalse();
        var second = await compiler.GetReferencePathsAsync(projectPath, ct);
        await Assert.That(second.Failure).IsNull();
        await Assert.That(second.PreserveSnippets).IsTrue();
        await Assert.That(ReferenceEquals(first.ReferenceSet, second.ReferenceSet)).IsTrue();
        await Assert.That(bridge.ConnectionCount).IsEqualTo(1);
        await Assert.That(bridge.CommandCount).IsEqualTo(2);
    }
}
