using System.Text.Json.Serialization;

namespace Conduit;

static class BridgeEndpointKinds
{
    public const string Editor = "editor";
    public const string Player = "player";
}

static class BridgeTransportKinds
{
    public const string NamedPipe = "named_pipe";
    public const string Fifo = "fifo";
}

sealed class BridgeEndpointDescriptor
{
    public int ProtocolVersion { get; set; } = BridgeProtocol.Version;

    public string EndpointKind { get; set; } = string.Empty;

    public string Transport { get; set; } = string.Empty;

    public string EndpointId { get; set; } = string.Empty;

    public string PipeName { get; set; } = string.Empty;

    public int ProcessId { get; set; }

    public string SessionInstanceId { get; set; } = string.Empty;

    public string HandoffToken { get; set; } = string.Empty;

    public string UnityVersion { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string BuildGuid { get; set; } = string.Empty;

    public string CloudProjectId { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public string StartedUtc { get; set; } = string.Empty;

    public string LastSeenUtc { get; set; } = string.Empty;

    public bool CanMonitorProcess { get; set; }

    public string[] Capabilities { get; set; } = [];

    [JsonIgnore]
    public string EndpointDirectoryPath { get; set; } = string.Empty;

    public string Selector => PlayerSelector.Format(ProcessId);
}

readonly record struct PlayerSelector(int ProcessId)
{
    const string Prefix = "player:";

    public static bool TryParse(string? value, out PlayerSelector selector)
    {
        selector = default;
        if (value is null
            || !value.StartsWith(Prefix, StringComparison.Ordinal)
            || !int.TryParse(value.AsSpan(Prefix.Length), out var processId)
            || processId <= 0)
            return false;

        selector = new(processId);
        return true;
    }

    public static string Format(int processId) => Prefix + processId;

    public override string ToString() => Format(ProcessId);
}

static class BridgeTarget
{
    public static string Normalize(string? value) =>
        PlayerSelector.TryParse(value, out var player)
            ? player.ToString()
            : ProjectPathNormalizer.Normalize(value);
}
