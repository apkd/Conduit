using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

public sealed class UnityBridgeClient(ILogger<UnityBridgeClient> logger)
{
    static readonly TimeSpan connectAttemptTimeout = TimeSpan.FromMilliseconds(750);
    static readonly TimeSpan initialConnectWindow = TimeSpan.FromSeconds(15);
    static readonly TimeSpan connectRetryDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan commandStartTimeout = TimeSpan.FromSeconds(5);
    static readonly UTF8Encoding utf8NoBom = new(false);
    static readonly byte[] newline = [(byte)'\n'];
    readonly ConcurrentDictionary<string, CachedConnectionEntry> connectionCache = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<BridgeClientResult> ProbeAsync(string projectPath, int? processIdHint, CancellationToken ct)
        => await ProbeAsync(projectPath, processIdHint, initialConnectWindow, ct);

    internal async Task<BridgeClientResult> ProbeAsync(string projectPath, int? processIdHint, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
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
                processIdHint,
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
        CancellationToken ct
    )
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
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
                    processIdHint,
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
                processIdHint,
                ct,
                command
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
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
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
        int? processIdHint,
        CancellationToken ct,
        BridgeCommand? commandToSend
    )
    {
        var monitoredProcessId = handshake.EditorProcessId > 0 ? handshake.EditorProcessId : processIdHint;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var effectiveToken = timeoutCts.Token;
        var commandSent = commandToSend is null;

        try
        {
            if (commandToSend is not null)
            {
                if (await connection.SendCommandAsync(requestId, commandToSend, effectiveToken) is { } sendFailure)
                    return sendFailure;

                commandSent = true;

                var startWaitTask = connection.WaitForCommandStartedAsync(requestId, commandType, commandSent, commandStartTimeout, effectiveToken, ct);
                if (monitoredProcessId is not > 0)
                {
                    var startOutcome = await startWaitTask;
                    if (startOutcome.Failure is { } startFailure)
                        return startFailure;

                    if (startOutcome.FinalResult is { } earlyResult)
                        return earlyResult;
                }
                else
                {
                    var processExitTask = WaitForProcessExitAsync(monitoredProcessId.Value, commandType, commandSent, effectiveToken);
                    var completedStartTask = await Task.WhenAny(startWaitTask, processExitTask);
                    if (ReferenceEquals(completedStartTask, processExitTask) && await processExitTask is { } processFailure)
                        return processFailure.WithHandshake(handshake);

                    var startOutcome = await startWaitTask;
                    if (startOutcome.Failure is { } startFailure)
                        return startFailure;

                    if (startOutcome.FinalResult is { } earlyResult)
                        return earlyResult;
                }
            }

            var waitForResultTask = connection.WaitForResultAsync(requestId, commandType, timeout, commandSent, effectiveToken, ct);
            if (monitoredProcessId is > 0)
            {
                var processExitTask = WaitForProcessExitAsync(monitoredProcessId.Value, commandType, commandSent, effectiveToken);
                var completedTask = await Task.WhenAny((Task)waitForResultTask, processExitTask);
                if (ReferenceEquals(completedTask, processExitTask) && await processExitTask is { } processFailure)
                    return processFailure.WithHandshake(handshake);
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
    }

    async Task<(BridgeClientConnection? Connection, BridgeClientResult Result)> TryConnectUntilReadyAsync(
        string projectPath,
        int? processIdHint,
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
                if (TryCreateProcessExitFailure(processIdHint, context: null, commandSent: false) is { } processFailure)
                    return (null, processFailure);

                var connectResult = await TryConnectAsync(projectPath, timeoutToken);
                if (connectResult.Connection is not null)
                    return connectResult;

                lastFailure = connectResult.Result;
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
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var pipeName = ConduitUtility.GetPipeName(normalizedProjectPath);
        NamedPipeClientStream? pipe = null;
        StreamReader? reader = null;

        try
        {
            pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)connectAttemptTimeout.TotalMilliseconds, ct);

            reader = new(pipe, utf8NoBom, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            try
            {
                var hello = BridgeMessage.CreateHello(new() { ProjectPath = normalizedProjectPath });

                await WritePayloadAsync(pipe, BridgeProtocol.Serialize(hello), ct);
            }
            catch (IOException exception)
            {
                logger.ZLogDebug($"Unity connection disconnected while sending the hello handshake for '{normalizedProjectPath}'.", exception);
                await DisposeConnectionAsync(pipe, reader);
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
                await DisposeConnectionAsync(pipe, reader);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }

            var payload = await reader.ReadLineAsync(ct);
            if (payload is null)
            {
                await DisposeConnectionAsync(pipe, reader);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }

            var response = BridgeProtocol.Deserialize(payload);
            if (response?.MessageType != BridgeMessageTypes.Hello || response.Project is null)
            {
                await DisposeConnectionAsync(pipe, reader);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.InvalidHandshake,
                    $"Unity returned an invalid hello handshake for '{normalizedProjectPath}'. This usually means the editor is reloading.",
                    commandSent: false
                ));
            }

            response.Project.ProjectPath = ProjectPathNormalizer.Normalize(response.Project.ProjectPath);
            if (!string.Equals(response.Project.ProjectPath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                await DisposeConnectionAsync(pipe, reader);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProjectMismatch,
                    $"Unity connection responded for '{response.Project.ProjectPath}' while '{normalizedProjectPath}' was requested.",
                    commandSent: false
                ));
            }

            return (new(pipe, reader, response.Project, logger), BridgeClientResult.Connected(response.Project));
        }
        catch (TimeoutException)
        {
            await DisposeConnectionAsync(pipe, reader);
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
            await DisposeConnectionAsync(pipe, reader);
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
            await DisposeConnectionAsync(pipe, reader);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch
        {
            await DisposeConnectionAsync(pipe, reader);
            throw;
        }
    }

    static async Task<BridgeClientResult?> WaitForProcessExitAsync(int processId, string? context, bool commandSent, CancellationToken ct)
    {
        var process = ConduitUtility.TryGetProcess(processId);
        if (process is null)
            return ProcessExited(processId, context, commandSent);

        try
        {
            await process.WaitForExitAsync(ct);
            return ProcessExited(processId, context, commandSent);
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

    static BridgeClientResult? TryCreateProcessExitFailure(int? processId, string? context, bool commandSent)
    {
        if (processId is not > 0)
            return null;

        using var process = ConduitUtility.TryGetProcess(processId.Value);
        return process is null ? ProcessExited(processId.Value, context, commandSent) : null;
    }

    static BridgeClientResult ProcessExited(int processId, string? context, bool commandSent) =>
        BridgeClientResult.Failure(
            handshake: null,
            BridgeRuntimeFailureKind.ProcessExited,
            $"Unity editor process {processId} exited {(string.IsNullOrWhiteSpace(context) ? "while running the command" : $"while '{context}' was running")}.",
            commandSent
        );

    static async Task DisposeConnectionAsync(NamedPipeClientStream? pipe, StreamReader? reader)
    {
        reader?.Dispose();
        if (pipe is not null)
            await pipe.DisposeAsync();
    }

    static async Task WritePayloadAsync(Stream stream, string payload, CancellationToken ct)
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

    internal sealed class BridgeClientConnection(
        NamedPipeClientStream pipe,
        StreamReader reader,
        BridgeProjectHandshake handshake,
        ILogger<UnityBridgeClient> logger) : IAsyncDisposable
    {
        BridgeProjectHandshake Handshake { get; } = handshake;

        public bool IsConnected => pipe.IsConnected;

        string DescribeRequest(string requestId, string commandType, string phase)
            => $"{phase} for request '{requestId}' ({commandType}) on pid {Handshake.EditorProcessId}, session {Handshake.SessionInstanceId}";

        public async Task<BridgeClientResult?> SendCommandAsync(string requestId, BridgeCommand command, CancellationToken ct)
        {
            try
            {
                await WritePayloadAsync(pipe, BridgeProtocol.Serialize(BridgeMessage.CreateCommand(requestId, command)), ct);
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

                while (!startTimeoutCts.IsCancellationRequested && pipe.IsConnected)
                {
                    var payload = await reader.ReadLineAsync(startTimeoutCts.Token);
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
                        return new(BridgeClientResult.Success(Handshake, response.Result.ToToolExecutionResult(), commandSent), null);
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
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    var payload = await reader.ReadLineAsync(ct);
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

                    return BridgeClientResult.Success(Handshake, response.Result.ToToolExecutionResult(), commandSent);
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
            reader.Dispose();
            await pipe.DisposeAsync();
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
