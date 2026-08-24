using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit;

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

        var environmentInspector = new UnityProjectEnvironmentInspector();
        var store = new RecentProjectStore(options, timeProvider);
        var registry = new UnityProjectRegistry(
            store,
            environmentInspector,
            options,
            timeProvider,
            NullLogger<UnityProjectRegistry>.Instance
        );
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

    [Test]
    public async Task EditorLogPathSurvivesOfflineSnapshotAndRegistryReload()
    {
        var timeProvider = new FakeTimeProvider(new(2026, 03, 20, 12, 0, 0, TimeSpan.Zero));
        var options = new ConduitOptions
        {
            StateDirectoryPath = tempDirectory,
            RecentProjectRetention = TimeSpan.FromDays(7),
        };
        var projectPath = ProjectPathNormalizer.Normalize(Path.Combine(tempDirectory, "SampleProject"));
        var editorLogPath = Path.Combine(projectPath, "Logs", "Editor.log");
        var environmentInspector = new UnityProjectEnvironmentInspector();
        var registry = new UnityProjectRegistry(
            new(options, timeProvider),
            environmentInspector,
            options,
            timeProvider,
            NullLogger<UnityProjectRegistry>.Instance
        );

        await registry.UpdateFromHandshakeAsync(
            new()
            {
                ProjectPath = projectPath,
                DisplayName = "SampleProject",
                UnityVersion = "6000.4.0f1",
                EditorProcessId = 1234,
                EditorLogPath = editorLogPath,
                SessionInstanceId = "session-1",
                LastSeenUtc = timeProvider.GetUtcNow(),
            },
            CancellationToken.None
        );

        var offlineSnapshot = new UnityProjectEnvironmentSnapshot(
            projectPath,
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Missing,
            runningUnityProcessCount: 0,
            matchedProcess: null
        );

        await Assert.That(environmentInspector.ResolveEditorLogPath(offlineSnapshot))
            .IsEqualTo(editorLogPath);

        var reloadedEnvironmentInspector = new UnityProjectEnvironmentInspector();
        _ = new UnityProjectRegistry(
            new(options, timeProvider),
            reloadedEnvironmentInspector,
            options,
            timeProvider,
            NullLogger<UnityProjectRegistry>.Instance
        );

        await Assert.That(reloadedEnvironmentInspector.ResolveEditorLogPath(offlineSnapshot))
            .IsEqualTo(editorLogPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }
}
