namespace Conduit;

// stdio clients own separate server processes, so restart exclusion must cross process boundaries
sealed class ProjectRestartLock(FileStream stream, bool wasContended) : IDisposable
{
    const int RetryDelayMilliseconds = 100;

    internal bool WasContended { get; } = wasContended;

    internal static async Task<ProjectRestartLock> AcquireAsync(string projectPath, CancellationToken ct)
    {
        string lockPath = GetLockPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        bool wasContended = false;

        while (true)
        {
            try
            {
                return new(
                    new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                    wasContended
                );
            }
            catch (IOException)
            {
                wasContended = true;
                await Task.Delay(RetryDelayMilliseconds, ct);
            }
        }
    }

    // library remains stable while Unity removes and recreates Temp during startup
    internal static string GetLockPath(string projectPath)
        => Path.Combine(
            ProjectPathNormalizer.ToPlatformPath(projectPath),
            "Library",
            "ConduitRestart.lock"
        );

    public void Dispose() => stream.Dispose();
}
