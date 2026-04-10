namespace Conduit;

public sealed class ConduitOptions
{
    public string PipeName { get; set; }
        = "unity-conduit";

    public TimeSpan RecentProjectRetention { get; set; }
        = TimeSpan.FromDays(7);

    public string StateDirectoryPath { get; set; }
        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), nameof(Conduit));

    public string RecentProjectsPath
        => Path.Combine(StateDirectoryPath, "recent-projects.json");
}
