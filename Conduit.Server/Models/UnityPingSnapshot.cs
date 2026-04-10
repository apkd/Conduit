namespace Conduit;

sealed class UnityPingSnapshot
{
    public string UnityVersion { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public int EditorProcessId { get; set; }

    public string Uptime { get; set; } = string.Empty;

    public string EditorMode { get; set; } = string.Empty;

    public bool IsPaused { get; set; }

    public bool IsCompiling { get; set; }

    public bool IsUpdating { get; set; }

    public string? ActiveCommandType { get; set; }

    public string[] Scenes { get; set; } = [];

    public string[] DirtyScenes { get; set; } = [];
}
