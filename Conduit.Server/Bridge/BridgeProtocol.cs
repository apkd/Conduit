using System.Text.Json;

namespace Conduit;

static class BridgeProtocol
{
    public const int Version = BridgeContract.Version;

    public static string Serialize(BridgeMessage message) =>
        JsonSerializer.Serialize(message, ConduitJsonContext.Default.BridgeMessage);

    public static BridgeMessage? Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize(payload, ConduitJsonContext.Default.BridgeMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
