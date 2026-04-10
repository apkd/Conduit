using JetBrains.Annotations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class UnityProjectRegistryTests : IDisposable
{
    readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "Conduit.Tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task ReachableProjectTransitionsBetweenConnectedAndOfflineSnapshots()
    {
        var timeProvider = new FakeTimeProvider(new(2026, 03, 20, 12, 0, 0, TimeSpan.Zero));
        var options = new ConduitOptions
        {
            StateDirectoryPath = tempDirectory,
            RecentProjectRetention = TimeSpan.FromDays(7),
        };

        var store = new RecentProjectStore(options, timeProvider);
        var registry = new UnityProjectRegistry(store, options, timeProvider, NullLogger<UnityProjectRegistry>.Instance);
        var handshake = new BridgeProjectHandshake
        {
            ProjectPath = "B:/Projects/MyGame/",
            DisplayName = "MyGame",
            UnityVersion = "6000.0.0f1",
            SessionInstanceId = "session-1",
            LastSeenUtc = timeProvider.GetUtcNow(),
        };

        await registry.UpdateFromHandshakeAsync(handshake, CancellationToken.None);

        var connectedProject = await Assert.That(registry.ListProjects()).HasSingleItem();
        await Assert.That(connectedProject.Status).IsEqualTo(ProjectStatus.ConnectedIdle);
        await Assert.That(connectedProject.LastSeenUtc).IsEqualTo("just now");
        await Assert.That(connectedProject.ProjectPath).IsEqualTo("/mnt/b/Projects/MyGame");

        registry.MarkReachable("/mnt/b/Projects/MyGame", false);

        var disconnectedProject = await Assert.That(registry.ListProjects()).HasSingleItem();
        await Assert.That(disconnectedProject.Status).IsEqualTo(ProjectStatus.Offline);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }
}
