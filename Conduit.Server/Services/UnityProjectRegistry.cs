using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ZLinq;
using ZLogger;

namespace Conduit;

public sealed class UnityProjectRegistry
{
    readonly RecentProjectStore recentProjectStore;
    readonly ConduitOptions options;
    readonly TimeProvider timeProvider;
    readonly ILogger<UnityProjectRegistry> logger;
    readonly ConcurrentDictionary<string, ProjectSession> projects = new(StringComparer.OrdinalIgnoreCase);

    public UnityProjectRegistry(
        RecentProjectStore recentProjectStore,
        ConduitOptions options,
        TimeProvider timeProvider,
        ILogger<UnityProjectRegistry> logger
    )
    {
        this.recentProjectStore = recentProjectStore;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;

        foreach (var record in this.recentProjectStore.Load())
        {
            var session = new ProjectSession(record);
            if (session.ProjectPath.Length > 0)
                projects[session.ProjectPath] = session;
        }
    }

    public ProjectListItem[] ListProjects()
    {
        if (PruneExpiredProjects())
            _ = PersistAsync(CancellationToken.None);

        var snapshot = GetOrderedProjectsSnapshot();
        var now = timeProvider.GetUtcNow();
        return snapshot
            .AsValueEnumerable()
            .Select(project => project.ToListItem(now))
            .ToArray();
    }

    internal ProjectSession[] SnapshotProjects()
    {
        if (PruneExpiredProjects())
            _ = PersistAsync(CancellationToken.None);

        return GetOrderedProjectsSnapshot();
    }

    internal string? GetLatestUnityVersion()
    {
        if (PruneExpiredProjects())
            _ = PersistAsync(CancellationToken.None);

        foreach (var project in GetOrderedProjectsSnapshot())
            if (!string.IsNullOrWhiteSpace(project.UnityVersion))
                return project.UnityVersion;

        return null;
    }

    internal ProjectSession GetOrAddProject(string projectPath)
    {
        if (PruneExpiredProjects())
            _ = PersistAsync(CancellationToken.None);

        var normalizedPath = ProjectPathNormalizer.Normalize(projectPath);
        return projects.GetOrAdd(normalizedPath, static path => new(path));
    }

    public void MarkReachable(string projectPath, bool reachable)
    {
        var normalizedPath = ProjectPathNormalizer.Normalize(projectPath);
        if (!projects.TryGetValue(normalizedPath, out var project))
        {
            if (!reachable)
                return;

            project = projects.GetOrAdd(normalizedPath, static path => new(path));
        }

        project.MarkReachable(reachable);
    }

    public async Task UpdateFromHandshakeAsync(BridgeProjectHandshake handshake, CancellationToken ct)
    {
        handshake.ProjectPath = ProjectPathNormalizer.Normalize(handshake.ProjectPath);
        if (string.IsNullOrWhiteSpace(handshake.ProjectPath))
            return;

        if (handshake.LastSeenUtc == default)
            handshake.LastSeenUtc = timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(handshake.DisplayName))
            handshake.DisplayName = Path.GetFileName(handshake.ProjectPath);

        var project = projects.GetOrAdd(handshake.ProjectPath, static path => new(path));
        if (!project.UpdateMetadata(handshake))
            return;

        logger.ZLogInformation(
            $"Unity project reachable: {handshake.ProjectPath} ({handshake.UnityVersion})"
        );

        await PersistAsync(ct);
    }

    internal async Task PersistAsync(CancellationToken ct)
    {
        try
        {
            await recentProjectStore.SaveAsync(
                GetOrderedProjectsSnapshot()
                    .AsValueEnumerable()
                    .Select(project => project.ToRecentProjectRecord())
                    .ToArray(),
                ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.ZLogWarning($"Failed to persist recent Unity project metadata", exception);
        }
    }

    bool PruneExpiredProjects()
    {
        var cutoff = timeProvider.GetUtcNow() - options.RecentProjectRetention;
        var removedAny = false;

        foreach (var entry in projects)
        {
            if (!entry.Value.CanExpire(cutoff))
                continue;

            removedAny |= projects.TryRemove(entry.Key, out _);
        }

        return removedAny;
    }

    ProjectSession[] GetOrderedProjectsSnapshot()
    {
        var snapshot = projects.Values.AsValueEnumerable().ToArray();
        Array.Sort(snapshot, static (left, right) => right.LastSeenUtc.CompareTo(left.LastSeenUtc));
        return snapshot;
    }
}
