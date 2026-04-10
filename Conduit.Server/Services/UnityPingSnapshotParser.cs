using System.Text.Json;

namespace Conduit;

static class UnityPingSnapshotParser
{
    public static bool TryParse(string payload, out UnityPingSnapshot pingSnapshot)
    {
        try
        {
            pingSnapshot = JsonSerializer.Deserialize(payload, ConduitJsonContext.Default.UnityPingSnapshot)
                           ?? new();

            return !string.IsNullOrWhiteSpace(pingSnapshot.UnityVersion);
        }
        catch (JsonException)
        {
            pingSnapshot = new();
            return false;
        }
    }
}
