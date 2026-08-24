namespace Conduit;

readonly record struct PlayerEndpointResolution(
    BridgeEndpointDescriptor? Endpoint,
    string? Diagnostic
)
{
    public bool IsAmbiguous =>
        Endpoint is null
        && Diagnostic?.StartsWith("Player selector", StringComparison.Ordinal) == true;

    public static PlayerEndpointResolution Found(BridgeEndpointDescriptor endpoint) =>
        new(endpoint, null);

    public static PlayerEndpointResolution NotFound(PlayerSelector selector) =>
        new(null, $"No live Unity player uses selector '{selector}'.");

    public static PlayerEndpointResolution Ambiguous(
        PlayerSelector selector,
        int endpointCount
    ) =>
        new(
            null,
            $"Player selector '{selector}' is ambiguous: {endpointCount} live player sessions use that process ID."
        );
}
