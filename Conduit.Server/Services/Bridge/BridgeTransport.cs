using System.Buffers;
using System.Text;

namespace Conduit;

sealed partial class BridgeTransport(
    Func<CancellationToken, ValueTask<string?>> readLineAsync,
    Func<string, CancellationToken, Task> writePayloadAsync,
    Func<bool> isConnected,
    Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    const int ErrorAgain = 11;
    const int ErrorInterrupted = 4;
    const int InitialLineBufferLength = 8192;
    const int PayloadWriteBufferLength = 64 * 1024;
    const int MaximumRetainedLineBufferLength = 1024 * 1024;
    static readonly UTF8Encoding utf8NoBom = new(false);
    static readonly byte[] newline = [(byte)'\n'];
    int disposed;

    internal bool IsConnected => Volatile.Read(ref disposed) == 0 && isConnected();

    internal static async Task<BridgeTransport> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        return OperatingSystem.IsWindows()
            ? await ConnectNamedPipeAsync(pipeName, timeout, ct)
            : await ConnectFifoAsync(
                ResolveEditorFifoEndpoint(pipeName),
                timeout,
                ct
            );
    }

    internal static Task<BridgeTransport> ConnectAsync(
        BridgeEndpointDescriptor endpoint,
        TimeSpan timeout,
        CancellationToken ct) =>
        endpoint.Transport switch
        {
            BridgeTransportKinds.NamedPipe when endpoint.PipeName.Length > 0
                => ConnectNamedPipeAsync(endpoint.PipeName, timeout, ct),
            BridgeTransportKinds.Fifo when endpoint.EndpointDirectoryPath.Length > 0
                => ConnectFifoAsync(endpoint.EndpointDirectoryPath, timeout, ct),
            _ => throw new IOException(
                $"Unity endpoint '{endpoint.EndpointId}' advertises unsupported transport '{endpoint.Transport}'."
            ),
        };

    internal ValueTask<string?> ReadLineAsync(CancellationToken ct) => readLineAsync(ct);

    internal Task WritePayloadAsync(string payload, CancellationToken ct) => writePayloadAsync(payload, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        await disposeAsync();
    }

    internal static BridgeTransport FromStream(Stream stream, Func<bool> isConnected, Func<ValueTask> disposeAsync)
        => FromStreams(stream, stream, isConnected, disposeAsync);

    internal static BridgeTransport FromStreams(
        Stream input,
        Stream output,
        Func<bool> isConnected,
        Func<ValueTask> disposeAsync)
    {
        var reader = new StreamReader(input, utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return new(
            reader.ReadLineAsync,
            (payload, ct) => WriteStreamPayloadAsync(output, payload, ct),
            isConnected,
            () => DisposeStreamAsync(reader, disposeAsync)
        );
    }

    static async ValueTask DisposeStreamAsync(StreamReader reader, Func<ValueTask> disposeAsync)
    {
        reader.Dispose();
        await disposeAsync();
    }

    static async Task WriteStreamPayloadAsync(Stream stream, string payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // keep ordinary commands in one write while bounding large-message scratch space.
        var small = payload.Length <= PayloadWriteBufferLength / 4;
        var buffer = ArrayPool<byte>.Shared.Rent(small
            ? Math.Max(1, utf8NoBom.GetByteCount(payload))
            : PayloadWriteBufferLength);
        try
        {
            if (small)
            {
                var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                await stream.WriteAsync(buffer.AsMemory(0, written), ct);
            }
            else
            {
                var encoder = utf8NoBom.GetEncoder();
                var offset = 0;
                bool completed;
                do
                {
                    ct.ThrowIfCancellationRequested();
                    // preserve surrogate pairs and replacement fallback across chunk boundaries.
                    encoder.Convert(
                        payload.AsSpan(offset),
                        buffer.AsSpan(0, PayloadWriteBufferLength),
                        flush: true,
                        out var charsUsed,
                        out var bytesUsed,
                        out completed
                    );
                    offset += charsUsed;
                    await stream.WriteAsync(buffer.AsMemory(0, bytesUsed), ct);
                }
                while (!completed);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await stream.WriteAsync(newline, ct); // distinct write preserves Wine/Proton FIFO framing
        await stream.FlushAsync(ct);
    }
}
