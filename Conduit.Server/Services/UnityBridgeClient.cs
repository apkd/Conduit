using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

public sealed class UnityBridgeClient
{
    static readonly TimeSpan connectAttemptTimeout = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan initialConnectWindow = TimeSpan.FromSeconds(15);
    static readonly TimeSpan connectRetryDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan commandStartTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan commandCancellationSendTimeout = TimeSpan.FromSeconds(2);
    static readonly UTF8Encoding utf8NoBom = new(false);
    static readonly byte[] newline = [(byte)'\n'];
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

    internal async Task<BridgeClientResult> ProbeAsync(string projectPath, int? processIdHint, CancellationToken ct)
        => await ProbeAsync(projectPath, processIdHint, initialConnectWindow, ct);

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

        await cacheEntry.Gate.WaitAsync(ct);
        try
        {
            var connection = cacheEntry.Connection;
            var handshake = cacheEntry.Handshake;
            var reusedConnection = connection is not null && handshake is not null && connection.IsConnected;
            if (!reusedConnection)
            {
                if (connection is not null || handshake is not null)
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

                connection = newConnection;
                handshake = newHandshake;
            }

            var result = await WaitForCommandResultAsync(
                connection!,
                handshake!,
                requestId,
                command.CommandType,
                timeout,
                ct,
                command,
                commandCancellation
            );

            if (result.FailureKind is null && connection!.IsConnected)
                cacheEntry.Set(connection, handshake!);
            else
                await cacheEntry.DisposeConnectionAsync(connection);

            return result;
        }
        finally
        {
            cacheEntry.Gate.Release();
        }
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

    async Task<BridgeClientResult> WaitForCommandResultAsync(
        BridgeClientConnection connection,
        BridgeProjectHandshake handshake,
        string requestId,
        string commandType,
        TimeSpan timeout,
        CancellationToken ct,
        BridgeCommand? commandToSend,
        CancellationToken commandCancellation
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var cancellationMonitorCts = new CancellationTokenSource();
        var effectiveToken = timeoutCts.Token;
        var commandSent = commandToSend is null;
        var cancellationTask = Task.CompletedTask;

        try
        {
            if (commandToSend is not null)
            {
                if (await connection.SendCommandAsync(requestId, commandToSend, effectiveToken) is { } sendFailure)
                    return sendFailure;

                commandSent = true;
                cancellationTask = SendCancellationWhenRequestedAsync(
                    connection,
                    requestId,
                    commandType,
                    commandCancellation,
                    cancellationMonitorCts.Token
                );

                var startWaitTask = connection.WaitForCommandStartedAsync(requestId, commandType, commandSent, commandStartTimeout, effectiveToken, ct);
                if (CreateProcessExitTask(handshake, commandType, commandSent, effectiveToken) is { } processExitStartTask)
                {
                    var completedStartTask = await Task.WhenAny(startWaitTask, processExitStartTask);
                    if (ReferenceEquals(completedStartTask, processExitStartTask) && await processExitStartTask is { } processFailure)
                        return processFailure;
                }

                var startOutcome = await startWaitTask;
                if (startOutcome.Failure is { } startFailure)
                    return startFailure;

                if (startOutcome.FinalResult is { } earlyResult)
                    return earlyResult;
            }

            var waitForResultTask = connection.WaitForResultAsync(requestId, commandType, timeout, commandSent, effectiveToken, ct);
            if (CreateProcessExitTask(handshake, commandType, commandSent, effectiveToken) is { } processExitResultTask)
            {
                var completedTask = await Task.WhenAny((Task)waitForResultTask, processExitResultTask);
                if (ReferenceEquals(completedTask, processExitResultTask) && await processExitResultTask is { } processFailure)
                    return processFailure;
            }

            return await waitForResultTask;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return commandSent
                ? BridgeClientResult.Failure(
                    handshake,
                    BridgeRuntimeFailureKind.ResultTimedOut,
                    $"Unity did not report completion for '{commandType}' within {timeout}.",
                    commandSent
                )
                : BridgeClientResult.Failure(
                    handshake,
                    BridgeRuntimeFailureKind.SendTimedOut,
                    $"Timed out while trying to send '{commandType}' to Unity.",
                    commandSent
                );
        }
        finally
        {
            if (!commandCancellation.IsCancellationRequested)
                cancellationMonitorCts.Cancel();

            await cancellationTask;
        }

        static async Task SendCancellationWhenRequestedAsync(
            BridgeClientConnection connection,
            string requestId,
            string commandType,
            CancellationToken commandCancellation,
            CancellationToken stopMonitoring
        )
        {
            if (!commandCancellation.CanBeCanceled)
                return;

            if (!commandCancellation.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                    commandCancellation,
                    stopMonitoring
                );

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token);
                }
                catch (OperationCanceledException) { }
            }

            if (!commandCancellation.IsCancellationRequested)
                return;

            using var sendCts = new CancellationTokenSource(commandCancellationSendTimeout);
            await connection.SendCancelCommandAsync(requestId, commandType, sendCts.Token);
        }
    }

    async Task<(BridgeClientConnection? Connection, BridgeClientResult Result)> TryConnectUntilReadyAsync(
        string projectPath,
        DateTimeOffset deadline,
        CancellationToken timeoutToken,
        CancellationToken callerToken
    )
    {
        BridgeClientResult? lastFailure = null;

        try
        {
            while (!timeoutToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                var connectResult = await TryConnectAsync(projectPath, timeoutToken);
                if (connectResult.Connection is not null)
                    return connectResult;

                lastFailure = connectResult.Result;
                if (lastFailure.FailureKind is BridgeRuntimeFailureKind.AmbiguousTarget
                    or BridgeRuntimeFailureKind.ProtocolMismatch)
                    break;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                var delay = remaining < connectRetryDelay ? remaining : connectRetryDelay;
                await Task.Delay(delay, timeoutToken);
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { }

        return (null, lastFailure ?? BridgeClientResult.Failure(
            handshake: null,
            BridgeRuntimeFailureKind.ConnectTimedOut,
            $"Could not establish a Unity connection for '{projectPath}' in time.",
            commandSent: false
        ));
    }

    async Task<(BridgeClientConnection? Connection, BridgeClientResult Result)> TryConnectAsync(string projectPath, CancellationToken ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        BridgeTransport? transport = null;

        try
        {
            BridgeEndpointDescriptor? endpoint = null;
            if (PlayerSelector.TryParse(normalizedProjectPath, out var playerSelector))
            {
                var resolution = await playerDiscovery.ResolveAsync(playerSelector, ct);
                if (resolution.Endpoint is null)
                    return (null, BridgeClientResult.Failure(
                        handshake: null,
                        resolution.IsAmbiguous
                            ? BridgeRuntimeFailureKind.AmbiguousTarget
                            : BridgeRuntimeFailureKind.ConnectTimedOut,
                        resolution.Diagnostic!,
                        commandSent: false
                    ));

                endpoint = resolution.Endpoint;
                transport = await BridgeTransport.ConnectAsync(endpoint, connectAttemptTimeout, ct);
            }
            else
            {
                var pipeName = ConduitUtility.GetPipeName(normalizedProjectPath);
                transport = await BridgeTransport.ConnectAsync(pipeName, connectAttemptTimeout, ct);
            }

            try
            {
                var hello = BridgeMessage.CreateHello(
                    endpoint is null
                        ? new() { ProjectPath = normalizedProjectPath }
                        : new()
                        {
                            EndpointKind = BridgeEndpointKinds.Player,
                            ProcessId = endpoint.ProcessId,
                            SessionInstanceId = endpoint.SessionInstanceId,
                        }
                );

                await transport.WritePayloadAsync(BridgeProtocol.Serialize(hello), ct);
            }
            catch (IOException exception)
            {
                logger.ZLogDebug($"Unity connection disconnected while sending the hello handshake for '{normalizedProjectPath}'.", exception);
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }
            catch (ObjectDisposedException exception)
            {
                logger.ZLogDebug($"Unity connection disposed the pipe while sending the hello handshake for '{normalizedProjectPath}'.", exception);
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }

            var payload = await transport.ReadLineAsync(ct);
            if (payload is null)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }

            var response = BridgeProtocol.Deserialize(payload);
            if (response?.MessageType != BridgeMessageTypes.Hello)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.InvalidHandshake,
                    $"Unity returned an invalid hello handshake for '{normalizedProjectPath}'. This usually means the editor is reloading.",
                    commandSent: false
                ));
            }

            if (response.ProtocolVersion != BridgeProtocol.Version)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProtocolMismatch,
                    BridgeContract.FormatProtocolMismatch(
                        BridgeProtocol.Version,
                        response.ProtocolVersion
                    ),
                    commandSent: false
                ));
            }

            if (response.Project is null)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.InvalidHandshake,
                    $"Unity returned an invalid hello handshake for '{normalizedProjectPath}'. This usually means the editor is reloading.",
                    commandSent: false
                ));
            }

            response.Project.ProjectPath = ProjectPathNormalizer.Normalize(response.Project.ProjectPath);
            if (endpoint is null
                && !string.Equals(response.Project.ProjectPath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProjectMismatch,
                    $"Unity connection responded for '{response.Project.ProjectPath}' while '{normalizedProjectPath}' was requested.",
                    commandSent: false
                ));
            }

            if (endpoint is not null
                && (response.Project.EndpointKind != BridgeEndpointKinds.Player
                    || response.Project.EffectiveProcessId != endpoint.ProcessId
                    || !string.Equals(
                        response.Project.SessionInstanceId,
                        endpoint.SessionInstanceId,
                        StringComparison.Ordinal
                    )))
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProjectMismatch,
                    $"The endpoint for '{normalizedProjectPath}' changed during its bridge handshake.",
                    commandSent: false
                ));
            }

            return (new(transport, response.Project, logger), BridgeClientResult.Connected(response.Project));
        }
        catch (TimeoutException)
        {
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch (IOException exception)
        {
            logger.ZLogDebug($"Unity connection attempt failed for '{normalizedProjectPath}'.", exception);
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch (ObjectDisposedException exception)
        {
            logger.ZLogDebug($"Unity connection was disposed while connecting to '{normalizedProjectPath}'.", exception);
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch (SocketException exception)
        {
            logger.ZLogDebug($"Unity socket connection attempt failed for '{normalizedProjectPath}'.", exception);
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch
        {
            await DisposeConnectionAsync(transport);
            throw;
        }
    }

    static Task<BridgeClientResult?>? CreateProcessExitTask(
        BridgeProjectHandshake handshake,
        string commandType,
        bool commandSent,
        CancellationToken ct
    )
    {
        if (!handshake.CanMonitorProcess || handshake.EffectiveProcessId is not > 0)
            return null;

        var process = ConduitUtility.TryGetProcess(handshake.EffectiveProcessId);
        return process is null
            ? null
            : WaitForProcessExitAsync(handshake, process, commandType, commandSent, ct);
    }

    static async Task<BridgeClientResult?> WaitForProcessExitAsync(
        BridgeProjectHandshake handshake,
        Process process,
        string context,
        bool commandSent,
        CancellationToken ct
    )
    {
        try
        {
            await process.WaitForExitAsync(ct);
            return ProcessExited(handshake, context, commandSent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    static BridgeClientResult ProcessExited(BridgeProjectHandshake handshake, string context, bool commandSent) =>
        BridgeClientResult.Failure(
            handshake,
            BridgeRuntimeFailureKind.ProcessExited,
            $"Unity {handshake.EndpointKind} process {handshake.EffectiveProcessId} exited while '{context}' was running.",
            commandSent
        );

    static async Task DisposeConnectionAsync(BridgeTransport? transport)
    {
        if (transport is not null)
            await transport.DisposeAsync();
    }

    static async Task WriteStreamPayloadAsync(Stream stream, string payload, CancellationToken ct)
    {
        var byteCount = utf8NoBom.GetByteCount(payload);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
            await stream.WriteAsync(buffer.AsMemory(0, written), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await stream.WriteAsync(newline, ct);
        await stream.FlushAsync(ct);
    }

    internal sealed class BridgeTransport(
        Func<CancellationToken, ValueTask<string?>> readLineAsync,
        Func<string, CancellationToken, Task> writePayloadAsync,
        Func<bool> isConnected,
        Func<ValueTask> disposeAsync) : IAsyncDisposable
    {
        const string DotNetUnixPipePrefix = "CoreFxPipe_";
        const int ErrorAgain = 11;
        const int ErrorInterrupted = 4;
        bool disposed;

        public bool IsConnected => !disposed && isConnected();

        public static async Task<BridgeTransport> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            return OperatingSystem.IsWindows()
                ? await ConnectNamedPipeAsync(pipeName, timeout, ct)
                : await ConnectFifoAsync(
                    ResolveEditorFifoEndpoint(pipeName),
                    timeout,
                    ct
                );
        }

        public static Task<BridgeTransport> ConnectAsync(
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

        public ValueTask<string?> ReadLineAsync(CancellationToken ct) => readLineAsync(ct);

        public Task WritePayloadAsync(string payload, CancellationToken ct) => writePayloadAsync(payload, ct);

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;

            disposed = true;
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

        internal static string GetDotNetUnixPipePath(string pipeName) =>
            Path.Combine(Path.GetTempPath(), DotNetUnixPipePrefix + pipeName);

        static async ValueTask DisposeStreamAsync(StreamReader reader, Func<ValueTask> disposeAsync)
        {
            reader.Dispose();
            await disposeAsync();
        }

        static async Task<BridgeTransport> ConnectNamedPipeAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);
                return FromStream(pipe, () => pipe.IsConnected, () => pipe.DisposeAsync());
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

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
            FileStream? response = null;

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
                response = new(
                    responsePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.Asynchronous
                );

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
                return FromStreams(
                    response,
                    request,
                    () => connected,
                    async () =>
                    {
                        connected = false;
                        await request.DisposeAsync();
                        await response.DisposeAsync();
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
                if (response is not null)
                    await response.DisposeAsync();
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

        [DllImport("libc", SetLastError = true)]
        static extern int mkfifo(string pathname, uint mode);

        [DllImport("libc", SetLastError = true)]
        static extern int open(string pathname, int flags);

        [DllImport("libc", SetLastError = true)]
        static extern int close(int fileDescriptor);

        static async Task<BridgeTransport> ConnectUnixSocketAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await ConnectSocketAsync(socket, GetDotNetUnixPipePath(pipeName), timeout, ct);
                var lineReader = new UnixSocketLineReader(socket);
                return new(
                    lineReader.ReadLineAsync,
                    (payload, writeToken) => WriteSocketPayloadAsync(socket, payload, writeToken),
                    static () => true,
                    () => DisposeSocketAsync(socket)
                );
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        static async Task ConnectSocketAsync(Socket socket, string path, TimeSpan timeout, CancellationToken ct)
        {
            var connectTask = socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
            var completedTask = await Task.WhenAny(connectTask, Task.Delay(timeout, ct));
            if (!ReferenceEquals(completedTask, connectTask))
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }

            await connectTask;
        }

        static ValueTask DisposeSocketAsync(Socket socket)
        {
            socket.Dispose();
            return ValueTask.CompletedTask;
        }

        static async Task WriteSocketPayloadAsync(Socket socket, string payload, CancellationToken ct)
        {
            var byteCount = utf8NoBom.GetByteCount(payload);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount + newline.Length);
            try
            {
                var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                newline.AsSpan().CopyTo(buffer.AsSpan(written));
                await SendAllAsync(socket, buffer.AsMemory(0, written + newline.Length), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            try
            {
                while (!payload.IsEmpty)
                {
                    await WaitForSocketAsync(socket, SelectMode.SelectWrite, ct);
                    var bytesSent = socket.Send(payload.Span, SocketFlags.None);
                    if (bytesSent <= 0)
                        throw new IOException("Unity socket closed while writing.");

                    payload = payload[bytesSent..];
                }
            }
            catch (SocketException exception)
            {
                throw new IOException("Unity socket closed while writing.", exception);
            }
        }

        static async ValueTask<int> ReceiveAsync(Socket socket, byte[] buffer, CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    await WaitForSocketAsync(socket, SelectMode.SelectRead, ct);

                    var bytesRead = PosixRead((int)socket.Handle, buffer, (nuint)buffer.Length);
                    if (bytesRead >= 0)
                        return (int)bytesRead;

                    var errno = Marshal.GetLastPInvokeError();
                    if (errno == ErrorInterrupted)
                        continue;

                    if (errno == ErrorAgain)
                    {
                        await Task.Yield();
                        continue;
                    }

                    throw new IOException($"Unity socket read failed with errno {errno}.");
                }
            }
            catch (SocketException exception)
            {
                throw new IOException("Unity socket closed while reading.", exception);
            }
        }

        static async ValueTask WaitForSocketAsync(Socket socket, SelectMode mode, CancellationToken ct)
        {
            while (!socket.Poll(100_000, mode))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            ct.ThrowIfCancellationRequested();
        }

#if CONDUIT_LINUX_MUSL
        [DllImport("*", EntryPoint = "read", SetLastError = true)]
#else
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
#endif
        static extern nint PosixRead(int fd, byte[] buffer, nuint count);

        sealed class UnixSocketLineReader(Socket socket)
        {
            readonly byte[] receiveBuffer = new byte[8192];
            byte[] pending = new byte[8192];
            int pendingCount;

            public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
            {
                try
                {
                    while (true)
                    {
                        if (TryReadBufferedLine(out var bufferedLine))
                            return bufferedLine;

                        var bytesRead = await ReceiveAsync(socket, receiveBuffer, ct);
                        if (bytesRead == 0)
                            return ReadRemainingLine();

                        Append(receiveBuffer.AsSpan(0, bytesRead));
                    }
                }
                catch (SocketException exception)
                {
                    throw new IOException("Unity socket closed while reading.", exception);
                }
            }

            bool TryReadBufferedLine(out string? line)
            {
                var newlineIndex = pending.AsSpan(0, pendingCount).IndexOf((byte)'\n');
                if (newlineIndex < 0)
                {
                    line = null;
                    return false;
                }

                line = DecodeLine(newlineIndex);
                var remaining = pendingCount - newlineIndex - 1;
                if (remaining > 0)
                    pending.AsSpan(newlineIndex + 1, remaining).CopyTo(pending);

                pendingCount = remaining;
                return true;
            }

            string? ReadRemainingLine()
            {
                if (pendingCount == 0)
                    return null;

                var line = DecodeLine(pendingCount);
                pendingCount = 0;
                return line;
            }

            string DecodeLine(int byteCount)
            {
                if (byteCount > 0 && pending[byteCount - 1] == '\r')
                    byteCount--;

                return utf8NoBom.GetString(pending, 0, byteCount);
            }

            void Append(ReadOnlySpan<byte> payload)
            {
                EnsureCapacity(pendingCount + payload.Length);
                payload.CopyTo(pending.AsSpan(pendingCount));
                pendingCount += payload.Length;
            }

            void EnsureCapacity(int required)
            {
                if (pending.Length >= required)
                    return;

                var size = pending.Length;
                while (size < required)
                    size *= 2;

                Array.Resize(ref pending, size);
            }
        }
    }

    internal sealed class BridgeClientConnection(
        BridgeTransport transport,
        BridgeProjectHandshake handshake,
        ILogger<UnityBridgeClient> logger) : IAsyncDisposable
    {
        BridgeProjectHandshake Handshake { get; } = handshake;

        public bool IsConnected => transport.IsConnected;

        string DescribeRequest(string requestId, string commandType, string phase)
            => $"{phase} for request '{requestId}' ({commandType}) on pid {Handshake.EffectiveProcessId}, session {Handshake.SessionInstanceId}";

        public async Task<BridgeClientResult?> SendCommandAsync(string requestId, BridgeCommand command, CancellationToken ct)
        {
            try
            {
                await transport.WritePayloadAsync(BridgeProtocol.Serialize(BridgeMessage.CreateCommand(requestId, command)), ct);
                return null;
            }
            catch (IOException exception)
            {
                logger.ZLogDebug($"Unity connection disconnected during {DescribeRequest(requestId, command.CommandType, BridgeMessageTypes.Command)}.", exception);
                return BridgeClientResult.Failure(
                    Handshake,
                    BridgeRuntimeFailureKind.SendFailed,
                    $"The Unity connection closed while sending '{command.CommandType}'.",
                    commandSent: false
                );
            }
            catch (ObjectDisposedException exception)
            {
                logger.ZLogDebug($"Unity connection disposed the pipe during {DescribeRequest(requestId, command.CommandType, BridgeMessageTypes.Command)}.", exception);
                return BridgeClientResult.Failure(
                    Handshake,
                    BridgeRuntimeFailureKind.SendFailed,
                    $"The Unity connection closed while sending '{command.CommandType}'.",
                    commandSent: false
                );
            }
        }

        public async Task SendCancelCommandAsync(string requestId, string commandType, CancellationToken ct)
        {
            try
            {
                await transport.WritePayloadAsync(
                    BridgeProtocol.Serialize(BridgeMessage.CreateCancelCommand(requestId)),
                    ct
                );
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                logger.ZLogDebug($"Could not send cancellation for {DescribeRequest(requestId, commandType, BridgeMessageTypes.CancelCommand)}.", exception);
            }
        }

        public async Task<CommandStartOutcome> WaitForCommandStartedAsync(
            string requestId,
            string commandType,
            bool commandSent,
            TimeSpan timeout,
            CancellationToken ct,
            CancellationToken callerToken
        )
        {
            try
            {
                using var startTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                startTimeoutCts.CancelAfter(timeout);

                while (!startTimeoutCts.IsCancellationRequested)
                {
                    var payload = await transport.ReadLineAsync(startTimeoutCts.Token);
                    if (payload is null)
                    {
                        return new(
                            FinalResult: null,
                            BridgeClientResult.Failure(
                                Handshake,
                                BridgeRuntimeFailureKind.StartAckDisconnected,
                                $"The Unity connection closed before '{commandType}' acknowledged starting.",
                                commandSent
                            )
                        );
                    }

                    var response = BridgeProtocol.Deserialize(payload);
                    if (response?.RequestId != requestId)
                        continue;

                    if (response?.MessageType == BridgeMessageTypes.CommandStarted)
                        return new(null, null);

                    // Reconnect replay may deliver the cached terminal result before a fresh
                    // command_started arrives. Accepting it here preserves single-completion
                    // semantics across transient disconnects.
                    if (response is { MessageType: BridgeMessageTypes.CommandResult, Result: not null })
                        return new(
                            BridgeClientResult.Success(
                                Handshake,
                                response.Result.ToToolExecutionResult(),
                                commandSent,
                                response.Result.Artifacts
                            ),
                            null
                        );
                }

                if (startTimeoutCts.IsCancellationRequested && callerToken.IsCancellationRequested)
                    callerToken.ThrowIfCancellationRequested();

                return new(
                    null,
                    BridgeClientResult.Failure(
                        Handshake,
                        startTimeoutCts.IsCancellationRequested
                            ? BridgeRuntimeFailureKind.StartAckTimedOut
                            : BridgeRuntimeFailureKind.StartAckDisconnected,
                        startTimeoutCts.IsCancellationRequested
                            ? $"Unity did not acknowledge starting '{commandType}' within {timeout}."
                            : $"The Unity connection closed before '{commandType}' acknowledged starting.",
                        commandSent
                    )
                );
            }
            catch (IOException exception)
            {
                logger.ZLogDebug($"Unity connection disconnected during {DescribeRequest(requestId, commandType, BridgeMessageTypes.CommandStarted)}.", exception);
                return new(
                    null,
                    BridgeClientResult.Failure(
                        Handshake,
                        BridgeRuntimeFailureKind.StartAckDisconnected,
                        $"The Unity connection closed before '{commandType}' acknowledged starting.",
                        commandSent
                    )
                );
            }
            catch (ObjectDisposedException exception)
            {
                logger.ZLogDebug($"Unity connection disposed the pipe during {DescribeRequest(requestId, commandType, BridgeMessageTypes.CommandStarted)}.", exception);
                return new(
                    null,
                    BridgeClientResult.Failure(
                        Handshake,
                        BridgeRuntimeFailureKind.StartAckDisconnected,
                        $"The Unity connection closed before '{commandType}' acknowledged starting.",
                        commandSent
                    )
                );
            }
        }

        public async Task<BridgeClientResult> WaitForResultAsync(
            string requestId,
            string commandType,
            TimeSpan timeout,
            bool commandSent,
            CancellationToken ct,
            CancellationToken callerToken
        )
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var payload = await transport.ReadLineAsync(ct);
                    if (payload is null)
                    {
                        return BridgeClientResult.Failure(
                            Handshake,
                            BridgeRuntimeFailureKind.ResultDisconnected,
                            $"The Unity connection closed before '{commandType}' reported completion.",
                            commandSent
                        );
                    }

                    var response = BridgeProtocol.Deserialize(payload);
                    if (response?.MessageType != BridgeMessageTypes.CommandResult || response.Result is null)
                        continue;

                    if (response.RequestId != requestId)
                        continue;

                    return BridgeClientResult.Success(
                        Handshake,
                        response.Result.ToToolExecutionResult(),
                        commandSent,
                        response.Result.Artifacts
                    );
                }

                if (ct.IsCancellationRequested && callerToken.IsCancellationRequested)
                    callerToken.ThrowIfCancellationRequested();

                return BridgeClientResult.Failure(
                    Handshake,
                    ct.IsCancellationRequested
                        ? BridgeRuntimeFailureKind.ResultTimedOut
                        : BridgeRuntimeFailureKind.ResultDisconnected,
                    ct.IsCancellationRequested
                        ? $"Unity did not report completion for '{commandType}' within {timeout}."
                        : $"The Unity connection closed before '{commandType}' reported completion.",
                    commandSent
                );
            }
            catch (IOException exception)
            {
                logger.ZLogDebug($"Unity connection disconnected during {DescribeRequest(requestId, commandType, BridgeMessageTypes.CommandResult)}.", exception);
                return BridgeClientResult.Failure(
                    Handshake,
                    BridgeRuntimeFailureKind.ResultDisconnected,
                    $"The Unity connection closed before '{commandType}' reported completion.",
                    commandSent
                );
            }
            catch (ObjectDisposedException exception)
            {
                logger.ZLogDebug($"Unity connection disposed the pipe during {DescribeRequest(requestId, commandType, BridgeMessageTypes.CommandResult)}.", exception);
                return BridgeClientResult.Failure(
                    Handshake,
                    BridgeRuntimeFailureKind.ResultDisconnected,
                    $"The Unity connection closed before '{commandType}' reported completion.",
                    commandSent
                );
            }
        }

        public async ValueTask DisposeAsync()
        {
            await transport.DisposeAsync();
        }

        public readonly record struct CommandStartOutcome(BridgeClientResult? FinalResult, BridgeClientResult? Failure);
    }

    sealed class CachedConnectionEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public BridgeClientConnection? Connection { get; private set; }

        public BridgeProjectHandshake? Handshake { get; private set; }

        public void Set(BridgeClientConnection connection, BridgeProjectHandshake handshake)
        {
            Connection = connection;
            Handshake = handshake;
        }

        public bool TryGetActive(out BridgeClientConnection? connection, out BridgeProjectHandshake? handshake)
        {
            connection = Connection;
            handshake = Handshake;
            return connection is not null && handshake is not null && connection.IsConnected;
        }

        public async Task DisposeConnectionAsync(BridgeClientConnection? expectedConnection = null)
        {
            var connection = Connection;
            if (connection is null || expectedConnection is not null && !ReferenceEquals(connection, expectedConnection))
                return;

            Connection = null;
            Handshake = null;
            await connection.DisposeAsync();
        }
    }
}
