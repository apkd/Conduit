namespace Conduit;

sealed class CachedConnectionEntry
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal BridgeClientConnection? Connection { get; private set; }

    internal BridgeProjectHandshake? Handshake { get; private set; }

    internal void Set(BridgeClientConnection connection, BridgeProjectHandshake handshake)
    {
        Connection = connection;
        Handshake = handshake;
    }

    internal bool TryGetActive(out BridgeClientConnection? connection, out BridgeProjectHandshake? handshake)
    {
        connection = Connection;
        handshake = Handshake;
        return connection is not null && handshake is not null && connection.IsConnected;
    }

    internal async Task DisposeConnectionAsync(BridgeClientConnection? expectedConnection = null)
    {
        var connection = Connection;
        if (connection is null || expectedConnection is not null && !ReferenceEquals(connection, expectedConnection))
            return;

        Connection = null;
        Handshake = null;
        await connection.DisposeAsync();
    }
}
