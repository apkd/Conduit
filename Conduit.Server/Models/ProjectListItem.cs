namespace Conduit;

public sealed class ProjectListItem
{
    public string ProjectPath { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string UnityVersion { get; init; } = string.Empty;
    public string LastSeenUtc { get; init; } = string.Empty;
    public string Status { get; init; } = ProjectStatus.Offline;
}
