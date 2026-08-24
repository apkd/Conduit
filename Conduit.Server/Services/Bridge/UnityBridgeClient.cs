using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Conduit;

public sealed partial class UnityBridgeClient
{
    static readonly TimeSpan connectAttemptTimeout = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan initialConnectWindow = TimeSpan.FromSeconds(15);
    static readonly TimeSpan connectRetryDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan commandCancellationSendTimeout = TimeSpan.FromSeconds(2);
    readonly ConcurrentDictionary<string, CachedConnectionEntry> connectionCache = new(StringComparer.OrdinalIgnoreCase);
    readonly UnityPlayerDiscovery playerDiscovery;
    readonly ILogger<UnityBridgeClient> logger;

    public UnityBridgeClient(ILogger<UnityBridgeClient> logger)
        : this(new(), logger) { }

    public UnityBridgeClient(UnityPlayerDiscovery playerDiscovery, ILogger<UnityBridgeClient> logger)
    {
        this.playerDiscovery = playerDiscovery;
        this.logger = logger;
    }

    internal Task<BridgeClientResult> ProbeAsync(string projectPath, int? processIdHint, CancellationToken ct)
        => ProbeAsync(projectPath, processIdHint, initialConnectWindow, ct);

    internal async Task<BridgeClientResult> ProbeAsync(string projectPath, int? processIdHint, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        var cacheEntry = connectionCache.GetOrAdd(normalizedProjectPath, static _ => new());
        var gateAcquired = false;

        try
        {
            await cacheEntry.Gate.WaitAsync(timeoutCts.Token);
            gateAcquired = true;
            if (cacheEntry.TryGetActive(out _, out var cachedHandshake))
                return BridgeClientResult.Connected(cachedHandshake!);
            if (cacheEntry.Connection is not null || cacheEntry.Handshake is not null)
                await cacheEntry.DisposeConnectionAsync();

            var connectResult = await TryConnectUntilReadyAsync(
                normalizedProjectPath,
                DateTimeOffset.UtcNow + timeout,
                timeoutCts.Token,
                ct
            );

            if (connectResult.Connection is { } connection && connectResult.Result.Handshake is { } handshake)
            {
                cacheEntry.Set(connection, handshake);
                return BridgeClientResult.Connected(handshake);
            }

            return connectResult.Result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            );
        }
        finally
        {
            if (gateAcquired)
                cacheEntry.Gate.Release();
        }
    }

    internal async Task<BridgeClientResult> ExecuteCommandAsync(
        string projectPath,
        string requestId,
        BridgeCommand command,
        TimeSpan timeout,
        int? processIdHint,
        CancellationToken ct,
        CancellationToken commandCancellation = default
    )
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        var cacheEntry = connectionCache.GetOrAdd(normalizedProjectPath, static _ => new());
        BridgeClientConnection connection;
        BridgeProjectHandshake handshake;

        await cacheEntry.Gate.WaitAsync(ct);
        try
        {
            if (!cacheEntry.TryGetActive(out var activeConnection, out var activeHandshake))
            {
                if (cacheEntry.Connection is not null || cacheEntry.Handshake is not null)
                    await cacheEntry.DisposeConnectionAsync();

                var effectiveInitialWindow = timeout < initialConnectWindow ? timeout : initialConnectWindow;
                using var initialWindowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                initialWindowCts.CancelAfter(effectiveInitialWindow);

                var connectResult = await TryConnectUntilReadyAsync(
                    normalizedProjectPath,
                    DateTimeOffset.UtcNow + effectiveInitialWindow,
                    initialWindowCts.Token,
                    ct
                );

                if (connectResult.Connection is not { } newConnection || connectResult.Result.Handshake is not { } newHandshake)
                    return connectResult.Result;

                cacheEntry.Set(newConnection, newHandshake);
                activeConnection = newConnection;
                activeHandshake = newHandshake;
            }

            connection = activeConnection!;
            handshake = activeHandshake!;
        }
        finally
        {
            cacheEntry.Gate.Release();
        }

        var result = await WaitForCommandResultAsync(
            connection,
            handshake,
            requestId,
            command.CommandType,
            timeout,
            command,
            commandCancellation,
            ct
        );

        // compilation phases exchange large player payloads; isolate each phase on a fresh fifo.
        if (!connection.IsConnected
            || command.CommandType is BridgeCommandTypes.CompilationReferences
                or BridgeCommandTypes.AssemblyBlob
            || result.FailureKind is BridgeRuntimeFailureKind.ProcessExited
                or BridgeRuntimeFailureKind.SendFailed
                or BridgeRuntimeFailureKind.SendTimedOut
                or BridgeRuntimeFailureKind.StartAckDisconnected
                or BridgeRuntimeFailureKind.StartAckTimedOut
                or BridgeRuntimeFailureKind.ResultDisconnected
                or BridgeRuntimeFailureKind.ResultTimedOut)
        {
            await cacheEntry.Gate.WaitAsync(CancellationToken.None);
            try
            {
                await cacheEntry.DisposeConnectionAsync(connection);
            }
            finally
            {
                cacheEntry.Gate.Release();
            }
        }

        return result;
    }

    internal bool TryGetLiveHandshake(string projectPath, out BridgeProjectHandshake? handshake)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        if (!connectionCache.TryGetValue(normalizedProjectPath, out var cacheEntry))
        {
            handshake = null;
            return false;
        }

        return cacheEntry.TryGetActive(out _, out handshake);
    }
}
