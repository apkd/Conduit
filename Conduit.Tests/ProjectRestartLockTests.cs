using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ProjectRestartLockTests
{
    [Test]
    public async Task SameProjectWaitsForTheActiveRestartProcess()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), $"conduit-restart-{Guid.NewGuid():N}");
        string lockPath = ProjectRestartLock.GetLockPath(projectPath);
        var first = await ProjectRestartLock.AcquireAsync(projectPath, CancellationToken.None);
        var secondTask = ProjectRestartLock.AcquireAsync(projectPath, CancellationToken.None);

        try
        {
            await Task.Delay(250);
            await Assert.That(secondTask.IsCompleted).IsFalse();
        }
        finally
        {
            first.Dispose();
        }

        using (var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10)))
            await Assert.That(second.WasContended).IsTrue();

        File.Delete(lockPath);
        Directory.Delete(projectPath, recursive: true);
    }

    [Test]
    public async Task EquivalentProjectPathsUseTheSameRestartLock()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), $"conduit-restart-{Guid.NewGuid():N}");

        await Assert.That(ProjectRestartLock.GetLockPath(projectPath))
            .IsEqualTo(ProjectRestartLock.GetLockPath(projectPath + Path.DirectorySeparatorChar));
    }
}
