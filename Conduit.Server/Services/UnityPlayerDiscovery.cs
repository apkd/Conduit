using System.Collections.Concurrent;
using System.Text.Json;

namespace Conduit;

/// <summary>Finds live player endpoints advertised through the local bridge registry.</summary>
public sealed class UnityPlayerDiscovery
{
    const int DescriptorCachePruneThreshold = 64;
    static readonly TimeSpan leaseLifetime = TimeSpan.FromSeconds(10);
    static readonly Lazy<string[]> defaultDiscoveryRoots = new(ConduitIpcPaths.GetDiscoveryRoots);
    internal static readonly TimeSpan ResolutionRetryDelay = TimeSpan.FromMilliseconds(25);
    readonly TimeProvider timeProvider;
    readonly Func<string[]> getDiscoveryRoots;
    readonly ConcurrentDictionary<string, CachedEndpointDescriptor> descriptorCache =
        new(StringComparer.Ordinal);

    public UnityPlayerDiscovery() : this(
        TimeProvider.System,
        static () => defaultDiscoveryRoots.Value
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
        List<BridgeEndpointDescriptor>? endpoints = null;
        foreach (var root in getDiscoveryRoots())
        {
            var endpointRoot = Path.Combine(root, "endpoints");
            if (!Directory.Exists(endpointRoot))
                continue;

            try
            {
                foreach (var endpointDirectory in Directory.EnumerateDirectories(endpointRoot))
                {
                    if (Path.GetFileName(endpointDirectory).StartsWith("editor-", StringComparison.Ordinal))
                        continue;

                    var descriptor = ReadDescriptor(endpointDirectory, out var lastSeen);
                    if (descriptor is null
                        || descriptor.ProtocolVersion != BridgeProtocol.Version
                        || descriptor.EndpointKind != BridgeEndpointKinds.Player
                        || descriptor.Platform.EndsWith("Editor", StringComparison.OrdinalIgnoreCase)
                        || descriptor.ProcessId <= 0
                        || now - lastSeen > leaseLifetime)
                        continue;

                    descriptor.EndpointDirectoryPath = endpointDirectory;
                    (endpoints ??= []).Add(descriptor);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // roots may disappear while Proton or another player is shutting down.
            }
        }

        if (descriptorCache.Count > DescriptorCachePruneThreshold)
            foreach (var path in descriptorCache.Keys)
                if (!File.Exists(path))
                    descriptorCache.TryRemove(path, out _);

        return endpoints is null ? [] : [.. endpoints];
    }

    internal BridgeEndpointDescriptor[] FindForProject(string projectPath)
    {
        var endpoints = Discover();
        if (endpoints.Length == 0)
            return endpoints;

        var identity = UnityProjectIdentity.Read(projectPath);
        List<BridgeEndpointDescriptor>? matches = null;
        foreach (var endpoint in endpoints)
            if (identity.Matches(endpoint))
                (matches ??= []).Add(endpoint);

        return matches is null ? [] : [.. matches];
    }

    internal PlayerEndpointResolution Resolve(PlayerSelector selector)
    {
        BridgeEndpointDescriptor? match = null;
        var matchCount = 0;
        foreach (var endpoint in Discover())
        {
            if (endpoint.ProcessId != selector.ProcessId)
                continue;

            match ??= endpoint;
            matchCount++;
        }

        return matchCount switch
        {
            0 => PlayerEndpointResolution.NotFound(selector),
            1 => PlayerEndpointResolution.Found(match!),
            _ => PlayerEndpointResolution.Ambiguous(selector, matchCount),
        };
    }

    internal async Task<PlayerEndpointResolution> ResolveAsync(
        PlayerSelector selector,
        CancellationToken ct)
    {
        var resolution = Resolve(selector);
        if (resolution.Endpoint is not null || resolution.IsAmbiguous)
            return resolution;

        await Task.Delay(ResolutionRetryDelay, timeProvider, ct);
        return Resolve(selector);
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
            foreach (var endpoint in Discover())
                if (endpoint.ProcessId == expectedProcessId
                    && string.Equals(
                        endpoint.HandoffToken,
                        handoffToken,
                        StringComparison.Ordinal
                    ))
                    return endpoint;

            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, ct);
        }

        return null;
    }

    BridgeEndpointDescriptor? ReadDescriptor(
        string endpointDirectory,
        out DateTimeOffset lastSeen)
    {
        lastSeen = default;
        try
        {
            var path = Path.Combine(endpointDirectory, "endpoint.json");
            var file = new FileInfo(path);
            var length = file.Length;
            var creationTimeUtc = file.CreationTimeUtc;
            var lastWriteUtc = file.LastWriteTimeUtc;
            if (descriptorCache.TryGetValue(path, out var cached)
                && cached.Length == length
                && cached.CreationTimeUtc == creationTimeUtc
                && cached.LastWriteUtc == lastWriteUtc)
            {
                lastSeen = cached.LastSeenUtc;
                return cached.Descriptor;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var descriptor = JsonSerializer.Deserialize(stream, ConduitJsonContext.Default.BridgeEndpointDescriptor);
            if (descriptor is null || !TryReadTimestamp(descriptor.LastSeenUtc, out lastSeen))
                return null;

            file.Refresh();
            if (file.Exists
                && file.Length == length
                && file.CreationTimeUtc == creationTimeUtc
                && file.LastWriteTimeUtc == lastWriteUtc)
                descriptorCache[path] = new(
                    length,
                    creationTimeUtc,
                    lastWriteUtc,
                    lastSeen,
                    descriptor
                );

            return descriptor;
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

    readonly record struct CachedEndpointDescriptor(
        long Length,
        DateTime CreationTimeUtc,
        DateTime LastWriteUtc,
        DateTimeOffset LastSeenUtc,
        BridgeEndpointDescriptor Descriptor
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
        int endpointCount) =>
        new(
            null,
            $"Player selector '{selector}' is ambiguous: {endpointCount} live player sessions use that process ID."
        );
}
