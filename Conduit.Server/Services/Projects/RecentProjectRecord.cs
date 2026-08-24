namespace Conduit;

public sealed class RecentProjectRecord
{
    public string ProjectPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UnityVersion { get; set; } = "";

    /// <summary>The last editor log path reported for this project.</summary>
    public string EditorLogPath { get; set; } = "";

    public DateTimeOffset LastSeenUtc { get; set; }
}
