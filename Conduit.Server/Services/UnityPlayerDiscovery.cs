using System.Text.Json;

namespace Conduit;

/// <summary>Finds live player endpoints advertised through the local bridge registry.</summary>
public sealed class UnityPlayerDiscovery
{
    static readonly TimeSpan leaseLifetime = TimeSpan.FromSeconds(10);
    readonly TimeProvider timeProvider;
    readonly Func<string[]> getDiscoveryRoots;

    public UnityPlayerDiscovery() : this(
        TimeProvider.System,
        ConduitIpcPaths.GetDiscoveryRoots
    ) { }

    internal UnityPlayerDiscovery(
        TimeProvider timeProvider,
        Func<string[]> getDiscoveryRoots)
    {
        this.timeProvider = timeProvider;
        this.getDiscoveryRoots = getDiscoveryRoots;
    }

    internal BridgeEndpointDescriptor[] Discover()
    {
        var now = timeProvider.GetUtcNow();
        var endpoints = new List<BridgeEndpointDescriptor>();
        foreach (var root in getDiscoveryRoots())
        {
            var endpointRoot = Path.Combine(root, "endpoints");
            if (!Directory.Exists(endpointRoot))
                continue;

            try
            {
                foreach (var endpointDirectory in Directory.EnumerateDirectories(endpointRoot))
                {
                    var descriptor = ReadDescriptor(endpointDirectory);
                    if (descriptor is null
                        || descriptor.ProtocolVersion != BridgeProtocol.Version
                        || descriptor.EndpointKind != BridgeEndpointKinds.Player
                        || descriptor.Platform.EndsWith("Editor", StringComparison.OrdinalIgnoreCase)
                        || descriptor.ProcessId <= 0
                        || !TryReadTimestamp(descriptor.LastSeenUtc, out var lastSeen)
                        || now - lastSeen > leaseLifetime)
                        continue;

                    descriptor.EndpointDirectoryPath = endpointDirectory;
                    endpoints.Add(descriptor);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // roots may disappear while Proton or another player is shutting down.
            }
        }

        return [.. endpoints];
    }

    internal BridgeEndpointDescriptor[] FindForProject(string projectPath)
    {
        var identity = UnityProjectIdentity.Read(projectPath);
        return [.. Discover().Where(identity.Matches)];
    }

    internal PlayerEndpointResolution Resolve(PlayerSelector selector)
    {
        var matches = Discover()
            .Where(endpoint => endpoint.ProcessId == selector.ProcessId)
            .ToArray();

        return matches.Length switch
        {
            0 => PlayerEndpointResolution.NotFound(selector),
            1 => PlayerEndpointResolution.Found(matches[0]),
            _ => PlayerEndpointResolution.Ambiguous(selector, matches),
        };
    }

    internal async Task<BridgeEndpointDescriptor?> WaitForHandoffAsync(
        string handoffToken,
        int expectedProcessId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            var endpoint = Discover().FirstOrDefault(value =>
                value.ProcessId == expectedProcessId
                && string.Equals(
                    value.HandoffToken,
                    handoffToken,
                    StringComparison.Ordinal
                )
            );
            if (endpoint is not null)
                return endpoint;

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, ct);
        }

        return null;
    }

    static BridgeEndpointDescriptor? ReadDescriptor(string endpointDirectory)
    {
        try
        {
            var path = Path.Combine(endpointDirectory, "endpoint.json");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, ConduitJsonContext.Default.BridgeEndpointDescriptor);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    static bool TryReadTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out timestamp
        );
}

readonly record struct PlayerEndpointResolution(
    BridgeEndpointDescriptor? Endpoint,
    string? Diagnostic)
{
    public bool IsAmbiguous => Endpoint is null && Diagnostic?.StartsWith("Player selector", StringComparison.Ordinal) == true;

    public static PlayerEndpointResolution Found(BridgeEndpointDescriptor endpoint) => new(endpoint, null);

    public static PlayerEndpointResolution NotFound(PlayerSelector selector) =>
        new(null, $"No live Unity player uses selector '{selector}'.");

    public static PlayerEndpointResolution Ambiguous(
        PlayerSelector selector,
        IReadOnlyCollection<BridgeEndpointDescriptor> endpoints) =>
        new(
            null,
            $"Player selector '{selector}' is ambiguous: {endpoints.Count} live player sessions use that process ID."
        );
}
