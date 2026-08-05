namespace Conduit;

sealed class UnityPingSnapshot
{
    public string UnityVersion { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public int EditorProcessId { get; set; }

    public string Uptime { get; set; } = string.Empty;

    public string EditorLogPath { get; set; } = string.Empty;

    public string EditorMode { get; set; } = string.Empty;

    public bool IsPaused { get; set; }

    public bool IsCompiling { get; set; }

    public bool IsUpdating { get; set; }

    public bool IsTestRunnerActive { get; set; }

    public string? ActiveTestMode { get; set; }

    public string? ActiveCommandType { get; set; }

    public int ActiveDetourCount { get; set; }

    public string[] ActiveDetours { get; set; } = [];

    public string? ProfilerStatusLine { get; set; }

    public string? RecordingStatusLine { get; set; }

    public string[] Scenes { get; set; } = [];

    public string[] DirtyScenes { get; set; } = [];
}
