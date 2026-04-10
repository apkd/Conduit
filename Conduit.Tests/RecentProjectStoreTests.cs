using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class RecentProjectStoreTests : IDisposable
{
    readonly string tempDirectory = Path.Combine(Path.GetTempPath(), "Conduit.Tests", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task SaveAsyncPrunesEntriesOlderThanRetention()
    {
        var timeProvider = new FakeTimeProvider(new(2026, 03, 20, 12, 0, 0, TimeSpan.Zero));
        var options = new ConduitOptions
        {
            StateDirectoryPath = tempDirectory,
            RecentProjectRetention = TimeSpan.FromDays(7),
        };

        var store = new RecentProjectStore(options, timeProvider);
        await store.SaveAsync(
            [
                new()
                {
                    ProjectPath = @"B:\Projects\Fresh",
                    DisplayName = "Fresh",
                    UnityVersion = "6000.0.0f1",
                    LastSeenUtc = timeProvider.GetUtcNow(),
                },
                new()
                {
                    ProjectPath = @"B:\Projects\Expired",
                    DisplayName = "Expired",
                    UnityVersion = "6000.0.0f1",
                    LastSeenUtc = timeProvider.GetUtcNow() - TimeSpan.FromDays(10),
                },
            ],
            CancellationToken.None
        );

        var loaded = store.Load();
        var project = await Assert.That(loaded).HasSingleItem();
        await Assert.That(project.ProjectPath).IsEqualTo("/mnt/b/Projects/Fresh");
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }
}
