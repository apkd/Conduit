using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conduit;

public sealed partial class UnityBridgeClientTests
{
    [Test]
    public async Task CompilationReferencesAreSingleFlightWithinAUnitySession()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-cache-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(projectPath, int.MaxValue);
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        var first = compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        var second = compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        await Assert.That(results.All(static result => result.Failure is null)).IsTrue();
        await Assert.That(bridge.CommandCount).IsEqualTo(1);
    }

    [Test]
    public async Task NewUnitySessionRefetchesCompilationReferences()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectPath = $"/tmp/conduit-reference-session-{Guid.NewGuid():N}";
        await using var bridge = await FakeFifoBridge.StartAsync(
            projectPath,
            int.MaxValue,
            changeSessionOnReconnect: true
        );
        var client = new UnityBridgeClient(NullLogger<UnityBridgeClient>.Instance);
        var compiler = new SnippetCompiler(client);

        await Assert.That(
            (await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None)).Failure
        ).IsNull();
        for (var attempt = 0; attempt < 100
             && client.TryGetLiveHandshake(projectPath, out _); attempt++)
            await Task.Delay(10);

        await Assert.That(
            (await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None)).Failure
        ).IsNull();
        await Assert.That(bridge.CommandCount).IsEqualTo(2);
    }

    [Test]
    public async Task CachedCompilationReferencesRefreshMutableMetadata()
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

        var first = await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        await Assert.That(first.Failure).IsNull();
        await Assert.That(first.PreserveSnippets).IsFalse();
        var second = await compiler.GetReferencePathsAsync(projectPath, CancellationToken.None);
        await Assert.That(second.Failure).IsNull();
        await Assert.That(second.PreserveSnippets).IsTrue();
        await Assert.That(bridge.CommandCount).IsEqualTo(2);
    }
}
