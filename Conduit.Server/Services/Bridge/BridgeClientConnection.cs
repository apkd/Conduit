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

sealed class BridgeClientConnection : IAsyncDisposable
{
    readonly BridgeTransport transport;
    readonly BridgeProjectHandshake handshake;
    readonly ILogger<UnityBridgeClient> logger;
    readonly string? endpointDirectory;
    readonly ConcurrentDictionary<string, PendingRequest> pendingRequests = new(StringComparer.Ordinal);
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly CancellationTokenSource receiveCts = new();
    readonly Task receiveTask;
    int disconnected;
    int disposed;

    internal BridgeClientConnection(
        BridgeTransport transport,
        BridgeProjectHandshake handshake,
        ILogger<UnityBridgeClient> logger,
        string? endpointDirectory = null)
    {
        this.transport = transport;
        this.handshake = handshake;
        this.logger = logger;
        this.endpointDirectory = endpointDirectory;
        receiveTask = ReceiveAsync();
    }

    internal bool IsConnected => Volatile.Read(ref disconnected) == 0;

    string DescribeRequest(string requestId, string commandType, string phase)
        => $"{phase} for request '{requestId}' ({commandType}) on pid {handshake.EffectiveProcessId}, session {handshake.SessionInstanceId}";

    internal PendingRequest RegisterRequest(string requestId, string commandType)
    {
        var pending = new PendingRequest(handshake, commandType);
        if (!pendingRequests.TryAdd(requestId, pending))
            throw new InvalidOperationException($"Bridge request '{requestId}' is already pending.");

        if (!IsConnected)
        {
            RemoveRequest(requestId, pending);
            pending.Disconnect();
        }

        return pending;
    }

    internal void RemoveRequest(string requestId, PendingRequest pending)
    {
        if (pendingRequests.TryGetValue(requestId, out var current)
            && ReferenceEquals(current, pending))
            pendingRequests.TryRemove(requestId, out _);
    }

    internal async Task<BridgeClientResult?> SendCommandAsync(
        string requestId,
        BridgeCommand command,
        CancellationToken ct)
    {
        try
        {
            await WriteMessageAsync(BridgeMessage.CreateCommand(requestId, command), ct);
            return null;
        }
        catch (IOException exception)
        {
            logger.ZLogDebug($"Unity connection disconnected during {DescribeRequest(requestId, command.CommandType, BridgeMessageTypes.Command)}.", exception);
            Disconnect();
            return SendFailure();
        }
        catch (ObjectDisposedException exception)
        {
            logger.ZLogDebug($"Unity connection disposed the pipe during {DescribeRequest(requestId, command.CommandType, BridgeMessageTypes.Command)}.", exception);
            Disconnect();
            return SendFailure();
        }

        BridgeClientResult SendFailure() => BridgeClientResult.Failure(
            handshake,
            BridgeRuntimeFailureKind.SendFailed,
            $"The Unity connection closed while sending '{command.CommandType}'.",
            commandSent: false
        );
    }

    internal async Task SendCancelCommandAsync(string requestId, string commandType, CancellationToken ct)
    {
        try
        {
            await WriteMessageAsync(BridgeMessage.CreateCancelCommand(requestId), ct);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            if (exception is IOException or ObjectDisposedException)
                Disconnect();
            logger.ZLogDebug($"Could not send cancellation for {DescribeRequest(requestId, commandType, BridgeMessageTypes.CancelCommand)}.", exception);
        }
    }

    async Task WriteMessageAsync(BridgeMessage message, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            if (!IsConnected)
                throw new IOException("The Unity bridge connection is closed.");

            await transport.WritePayloadAsync(BridgeProtocol.Serialize(message), ct);
        }
        finally
        {
            writeGate.Release();
        }
    }

    internal async Task<CommandStartOutcome> WaitForCommandStartedAsync(
        PendingRequest pending,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try
        {
            return await pending.Started.WaitAsync(timeoutToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return new(
                null,
                BridgeClientResult.Failure(
                    handshake,
                    IsConnected
                        ? BridgeRuntimeFailureKind.StartAckTimedOut
                        : BridgeRuntimeFailureKind.StartAckDisconnected,
                    IsConnected
                        ? $"Unity did not acknowledge starting '{pending.CommandType}' before the command deadline."
                        : $"The Unity connection closed before '{pending.CommandType}' acknowledged starting.",
                    pending.CommandSent
                )
            );
        }
    }

    internal async Task<BridgeClientResult> WaitForResultAsync(
        PendingRequest pending,
        TimeSpan timeout,
        CancellationToken ct,
        CancellationToken callerToken)
    {
        try
        {
            return await pending.Result.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            return BridgeClientResult.Failure(
                handshake,
                IsConnected
                    ? BridgeRuntimeFailureKind.ResultTimedOut
                    : BridgeRuntimeFailureKind.ResultDisconnected,
                IsConnected
                    ? $"Unity did not report completion for '{pending.CommandType}' within {timeout}."
                    : $"The Unity connection closed before '{pending.CommandType}' reported completion.",
                pending.CommandSent
            );
        }
    }

    async Task ReceiveAsync()
    {
        try
        {
            while (!receiveCts.IsCancellationRequested)
            {
                var payload = await transport.ReadLineAsync(receiveCts.Token);
                if (payload is null)
                    break;

                var message = BridgeProtocol.Deserialize(payload);
                if (message?.RequestId is not { Length: > 0 } requestId
                    || !pendingRequests.TryGetValue(requestId, out var pending))
                    continue;

                if (message.MessageType == BridgeMessageTypes.CommandStarted)
                {
                    pending.MarkStarted();
                    continue;
                }

                if (message is not { MessageType: BridgeMessageTypes.CommandResult, Result: not null })
                    continue;

                pending.Complete(CreateResult(message.Result));
                RemoveRequest(requestId, pending);
            }
        }
        catch (OperationCanceledException) when (receiveCts.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.ZLogDebug($"Unity bridge receive loop disconnected from pid {handshake.EffectiveProcessId}, session {handshake.SessionInstanceId}.", exception);
        }
        finally
        {
            Disconnect();
            await transport.DisposeAsync();
        }

        BridgeClientResult CreateResult(BridgeCommandResult result)
        {
            try
            {
                if (result.Artifacts.Length > 0 && handshake.IsPlayer)
                {
                    if (string.IsNullOrWhiteSpace(endpointDirectory))
                        throw new InvalidDataException("The player endpoint directory is unavailable.");

                    foreach (var artifact in result.Artifacts)
                        artifact.ResolveInEndpoint(endpointDirectory);
                }
                else if (result.Artifacts.Length > 0)
                    foreach (var artifact in result.Artifacts)
                        artifact.ResolveInProject(
                            ProjectPathNormalizer.ToPlatformPath(handshake.ProjectPath)
                        );

                return BridgeClientResult.Success(
                    handshake,
                    result.ToToolExecutionResult(),
                    commandSent: true,
                    result.Artifacts
                );
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
            {
                return BridgeClientResult.Success(
                    handshake,
                    ToolExecutionResult.FromException(
                        exception,
                        result.Logs,
                        "Unity returned an invalid shared artifact."
                    ),
                    commandSent: true,
                    []
                );
            }
        }
    }

    void Disconnect()
    {
        if (Interlocked.Exchange(ref disconnected, 1) != 0)
            return;

        foreach (var pending in pendingRequests.Values)
            pending.Disconnect();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        receiveCts.Cancel();
        Disconnect();
        await transport.DisposeAsync();
        try
        {
            await receiveTask;
        }
        catch (OperationCanceledException) { }

        receiveCts.Dispose();
        writeGate.Dispose();
    }

    internal sealed class PendingRequest(
        BridgeProjectHandshake handshake,
        string commandType)
    {
        readonly TaskCompletionSource<CommandStartOutcome> started
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource<BridgeClientResult> result
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int commandSent;
        int commandStarted;

        internal string CommandType { get; } = commandType;
        internal bool CommandSent => Volatile.Read(ref commandSent) != 0;
        internal Task<CommandStartOutcome> Started => started.Task;
        internal Task<BridgeClientResult> Result => result.Task;

        internal void MarkSent() => Volatile.Write(ref commandSent, 1);

        internal void MarkStarted()
        {
            Volatile.Write(ref commandStarted, 1);
            started.TrySetResult(new(null, null));
        }

        internal void Complete(BridgeClientResult finalResult)
        {
            Volatile.Write(ref commandSent, 1);
            // replayed results can arrive before command_started after a reconnect.
            if (Interlocked.Exchange(ref commandStarted, 1) == 0)
                started.TrySetResult(new(finalResult, null));
            result.TrySetResult(finalResult);
        }

        internal void Disconnect()
        {
            var wasStarted = Volatile.Read(ref commandStarted) != 0;
            var failure = BridgeClientResult.Failure(
                handshake,
                wasStarted
                    ? BridgeRuntimeFailureKind.ResultDisconnected
                    : BridgeRuntimeFailureKind.StartAckDisconnected,
                wasStarted
                    ? $"The Unity connection closed before '{CommandType}' reported completion."
                    : $"The Unity connection closed before '{CommandType}' acknowledged starting.",
                CommandSent
            );
            started.TrySetResult(new(null, failure));
            result.TrySetResult(failure);
        }
    }

    internal readonly record struct CommandStartOutcome(BridgeClientResult? FinalResult, BridgeClientResult? Failure);
}
