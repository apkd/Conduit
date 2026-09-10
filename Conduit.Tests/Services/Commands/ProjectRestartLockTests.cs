namespace Conduit;

public sealed class ProjectRestartLockTests
{
    [Test]
    [Timeout(60_000)]
    public async Task SameProjectWaitsForTheActiveRestartProcess(CancellationToken ct)
    {
        string projectPath = Path.Combine(Path.GetTempPath(), $"conduit-restart-{Guid.NewGuid():N}");
        Task<ProjectRestartLock> secondTask;
        bool wasBlocked;
        using (var first = await ProjectRestartLock.AcquireAsync(projectPath, ct))
        {
            secondTask = ProjectRestartLock.AcquireAsync(projectPath, ct);
            // acquisition tries the file lock before yielding, so contention is already established
            wasBlocked = !secondTask.IsCompleted;
        }

        try
        {
            using var second = await secondTask;
            await Assert.That(wasBlocked).IsTrue();
            await Assert.That(second.WasContended).IsTrue();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task EquivalentProjectPathsUseTheSameRestartLock()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), $"conduit-restart-{Guid.NewGuid():N}");

        await Assert.That(ProjectRestartLock.GetLockPath(projectPath))
            .IsEqualTo(ProjectRestartLock.GetLockPath(projectPath + Path.DirectorySeparatorChar));
    }
}
