namespace Conduit;

public static class ConduitEnvironment
{
    public const string Prefix = "UNITY_CONDUIT_";
}

public sealed class ConduitHostConfiguration
{
    public string? PipeName { get; init; }

    public string? StateDirectoryPath { get; init; }

    public int? RecentProjectRetentionDays { get; init; }

    public ConduitOptions ToOptions()
    {
        var options = new ConduitOptions();

        if (PipeName is { Length: > 0 })
            options.PipeName = PipeName;

        if (StateDirectoryPath is { Length: > 0 })
            options.StateDirectoryPath = StateDirectoryPath;

        if (RecentProjectRetentionDays is > 0)
            options.RecentProjectRetention = TimeSpan.FromDays(RecentProjectRetentionDays.Value);

        return options;
    }
}
