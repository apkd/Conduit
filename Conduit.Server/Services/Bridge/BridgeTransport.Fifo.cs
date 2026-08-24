using System.Runtime.InteropServices;

namespace Conduit;

sealed partial class BridgeTransport
{
    static async Task<BridgeTransport> ConnectFifoAsync(
        string endpointDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("POSIX FIFOs require a Linux MCP server.");
        if (!Directory.Exists(endpointDirectory))
            throw new IOException(
                $"Unity FIFO endpoint '{endpointDirectory}' is unavailable."
            );

        var clientsDirectory = Path.Combine(endpointDirectory, "clients");
        Directory.CreateDirectory(clientsDirectory);
        var clientDirectory = Path.Combine(clientsDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clientDirectory);
        TrySetDirectoryMode(clientDirectory);

        var requestPath = Path.Combine(clientDirectory, "to-unity.fifo");
        var responsePath = Path.Combine(clientDirectory, "from-unity.fifo");
        CreateFifo(requestPath);
        CreateFifo(responsePath);

        var requestKeeper = OpenFifoKeeper(requestPath);
        var responseKeeper = OpenFifoKeeper(responsePath);
        FileStream? request = null;
        FifoLineReader? response = null;

        try
        {
            var publicationPath = Path.Combine(clientDirectory, "request.json");
            await File.WriteAllTextAsync(
                publicationPath + ".tmp",
                $$"""{"protocol_version":{{BridgeProtocol.Version}}}""",
                utf8NoBom,
                ct
            );
            File.Move(publicationPath + ".tmp", publicationPath);

            request = new(
                requestPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.Asynchronous
            );
            response = FifoLineReader.Open(responsePath);

            var connectedPath = Path.Combine(clientDirectory, "connected");
            var deadline = DateTime.UtcNow + timeout;
            while (!File.Exists(connectedPath))
            {
                ct.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Unity did not accept FIFO client '{clientDirectory}' in time."
                    );

                await Task.Delay(10, ct);
            }

            CloseFifoKeeper(ref requestKeeper);
            CloseFifoKeeper(ref responseKeeper);
            var connected = true;
            return new(
                response.ReadLineAsync,
                (payload, writeToken) => WriteStreamPayloadAsync(request, payload, writeToken),
                () => connected,
                async () =>
                {
                    connected = false;
                    response.Dispose();
                    await request.DisposeAsync();
                    TryDeleteClientDirectory(clientDirectory);
                }
            );
        }
        catch
        {
            CloseFifoKeeper(ref requestKeeper);
            CloseFifoKeeper(ref responseKeeper);
            if (request is not null)
                await request.DisposeAsync();
            response?.Dispose();
            TryDeleteClientDirectory(clientDirectory);
            throw;
        }
    }

    static string ResolveEditorFifoEndpoint(string pipeName)
    {
        string? fallback = null;
        foreach (var root in ConduitIpcPaths.GetDiscoveryRoots())
        {
            var path = ConduitIpcPaths.GetEndpointDirectory(
                root,
                "editor-" + pipeName
            );
            fallback ??= path;
            if (Directory.Exists(path))
                return path;
        }

        return fallback
               ?? throw new IOException(
                   "No IPC root is available for the Unity Editor FIFO endpoint."
               );
    }

    static void CreateFifo(string path)
    {
        const uint userReadWrite = 0x180;
        if (mkfifo(path, userReadWrite) != 0)
            throw new IOException(
                $"Could not create FIFO '{path}' (errno {Marshal.GetLastPInvokeError()})."
            );
    }

    static int OpenFifoKeeper(string path)
    {
        const int readWrite = 2;
        const int nonBlocking = 0x800;
        const int closeOnExec = 0x80000;
        var descriptor = open(path, readWrite | nonBlocking | closeOnExec);
        if (descriptor < 0)
            throw new IOException(
                $"Could not open FIFO keeper '{path}' (errno {Marshal.GetLastPInvokeError()})."
            );

        return descriptor;
    }

    static void CloseFifoKeeper(ref int descriptor)
    {
        if (descriptor < 0)
            return;

        close(descriptor);
        descriptor = -1;
    }

    static void TrySetDirectoryMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    static void TryDeleteClientDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
    }

    // FileStream cannot cancel a pending FIFO read on Linux. Polling a nonblocking
    // descriptor keeps bridge command deadlines enforceable when a peer stops responding.
    sealed class FifoLineReader(int descriptor) : IDisposable
    {
        const int RetryDelayMilliseconds = 10;
        readonly byte[] readBuffer = new byte[4096];
        readonly MemoryStream lineBuffer = new(InitialLineBufferLength);
        readonly Queue<string> lines = new();
        readonly PeriodicTimer retryTimer = new(
            TimeSpan.FromMilliseconds(RetryDelayMilliseconds)
        );
        int descriptor = descriptor;
        bool endOfStream;

        internal static FifoLineReader Open(string path)
        {
            const int readOnly = 0;
            const int nonBlocking = 0x800;
            const int closeOnExec = 0x80000;
            var descriptor = open(path, readOnly | nonBlocking | closeOnExec);
            if (descriptor < 0)
                throw new IOException(
                    $"Could not open FIFO reader '{path}' (errno {Marshal.GetLastPInvokeError()})."
                );

            return new(descriptor);
        }

        internal async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (lines.TryDequeue(out var line))
                    return line;
                if (endOfStream)
                    return TakeIncompleteLine();
                if (descriptor < 0)
                    throw new ObjectDisposedException(nameof(FifoLineReader));

                var count = read(descriptor, readBuffer, (nuint)readBuffer.Length);
                if (count > 0)
                {
                    Append(readBuffer.AsSpan(0, checked((int)count)));
                    continue;
                }

                if (count == 0)
                {
                    endOfStream = true;
                    continue;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorInterrupted)
                    continue;
                if (error != ErrorAgain)
                    throw new IOException($"Could not read the Unity FIFO response (errno {error}).");

                await retryTimer.WaitForNextTickAsync(ct);
            }
        }

        void Append(ReadOnlySpan<byte> bytes)
        {
            var start = 0;
            for (var index = 0; index < bytes.Length; ++index)
            {
                if (bytes[index] != (byte)'\n')
                    continue;

                lineBuffer.Write(bytes[start..index]);
                lines.Enqueue(TakeLine());
                start = index + 1;
            }

            lineBuffer.Write(bytes[start..]);
        }

        string? TakeIncompleteLine() => lineBuffer.Length == 0 ? null : TakeLine();

        string TakeLine()
        {
            var bytes = lineBuffer.GetBuffer().AsSpan(0, checked((int)lineBuffer.Length));
            if (bytes is [.., (byte)'\r'])
                bytes = bytes[..^1];
            var line = utf8NoBom.GetString(bytes);
            lineBuffer.SetLength(0);
            if (lineBuffer.Capacity > MaximumRetainedLineBufferLength)
                lineBuffer.Capacity = InitialLineBufferLength;
            return line;
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref descriptor, -1);
            if (current >= 0)
                close(current);
            retryTimer.Dispose();
            lineBuffer.Dispose();
        }
    }

    [DllImport("libc", SetLastError = true)]
    static extern int mkfifo(string pathname, uint mode);

    [DllImport("libc", SetLastError = true)]
    static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern nint read(int descriptor, [Out] byte[] buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fileDescriptor);
}
