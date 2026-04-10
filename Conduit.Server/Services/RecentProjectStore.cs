using System.Text.Json;

namespace Conduit;

public sealed class RecentProjectStore(ConduitOptions options, TimeProvider timeProvider)
{
    readonly SemaphoreSlim writeGate = new(1, 1);

    public RecentProjectRecord[] Load()
    {
        if (!File.Exists(options.RecentProjectsPath))
            return [];

        try
        {
            var payload = File.ReadAllText(options.RecentProjectsPath);
            var document = JsonSerializer.Deserialize(payload, ConduitJsonContext.Default.RecentProjectDocument)
                           ?? new RecentProjectDocument();

            return Prune(document.Projects);
        }
        catch (Exception)
        {
            return [];
        }
    }

    public async Task SaveAsync(RecentProjectRecord[] records, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(
            value: new() { Projects = Prune(records) },
            jsonTypeInfo: ConduitJsonContext.Default.RecentProjectDocument
        );

        Directory.CreateDirectory(options.StateDirectoryPath);

        await writeGate.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(options.RecentProjectsPath, payload, ct);
        }
        finally
        {
            writeGate.Release();
        }
    }

    RecentProjectRecord[] Prune(IEnumerable<RecentProjectRecord> records)
    {
        var cutoff = timeProvider.GetUtcNow() - options.RecentProjectRetention;
        var latestByPath = new Dictionary<string, RecentProjectRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var normalizedProjectPath = ProjectPathNormalizer.Normalize(record.ProjectPath);
            if (record.LastSeenUtc < cutoff || normalizedProjectPath.Length == 0)
                continue;

            var normalizedRecord = new RecentProjectRecord
            {
                ProjectPath = normalizedProjectPath,
                DisplayName = record.DisplayName,
                UnityVersion = record.UnityVersion,
                LastSeenUtc = record.LastSeenUtc,
            };

            if (!latestByPath.TryGetValue(normalizedProjectPath, out var existing)
                || normalizedRecord.LastSeenUtc > existing.LastSeenUtc)
                latestByPath[normalizedProjectPath] = normalizedRecord;
        }

        var pruned = new RecentProjectRecord[latestByPath.Count];
        var index = 0;
        foreach (var record in latestByPath.Values)
            pruned[index++] = record;

        Array.Sort(pruned, static (left, right) => right.LastSeenUtc.CompareTo(left.LastSeenUtc));
        return pruned;
    }
}

sealed class RecentProjectDocument
{
    public RecentProjectRecord[] Projects { get; set; } = [];
}

public sealed class RecentProjectRecord
{
    public string ProjectPath { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string UnityVersion { get; set; } = "";

    public DateTimeOffset LastSeenUtc { get; set; }
}
