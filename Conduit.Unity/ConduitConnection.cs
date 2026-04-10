#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class ConduitConnection
    {
        const int MaxConcurrentClients = 254;
        static readonly ConcurrentQueue<InboundClientMessage> inboundMessages = new();
        static readonly ConcurrentDictionary<int, ClientSession> clientSessions = new();
        static readonly UTF8Encoding utf8NoBom = new(false);
        static readonly byte[] newline = { (byte)'\n' };
        static readonly TimeSpan sendTimeout = TimeSpan.FromSeconds(5);
        static readonly TimeSpan idleReceiveTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan recentAttachmentCooldown = TimeSpan.FromHours(1);
        static readonly object gate = new();
        static CancellationTokenSource? serverLoopCts;
        static bool started;
        static bool shuttingDown;
        static bool toolbarRefreshRequested;
        static ConduitConnectionStatus status;
        static DateTimeOffset attachedUntilUtc;
        static int nextClientId;
        static readonly string sessionInstanceId = Guid.NewGuid().ToString("N");

        public static void EnsureStarted()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;

            if (started)
                return;

            started = true;
            shuttingDown = false;
            UpdateConnectionStatus(ConduitConnectionStatus.Disconnected);
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            serverLoopCts = new();
            _ = RunServerLoopAsync(serverLoopCts.Token);
        }

        internal static ConduitConnectionStatus GetConnectionStatus()
        {
            lock (gate)
                return GetDisplayStatus(DateTimeOffset.UtcNow);
        }

        public static Task<bool> TrySendCommandStartedAsync(int clientId, string requestId, string? commandType = null)
        {
            using var timeoutCts = new CancellationTokenSource(sendTimeout);
            return TrySendMessageAsync(clientId, BridgeMessage.CreateCommandStarted(requestId), timeoutCts.Token, commandType);
        }

        public static Task<bool> TrySendResultAsync(int clientId, string requestId, BridgeCommandResult result, string? commandType = null)
        {
            using var timeoutCts = new CancellationTokenSource(sendTimeout);
            return TrySendMessageAsync(clientId, BridgeMessage.CreateCommandResult(requestId, result), timeoutCts.Token, commandType);
        }

        static async Task RunServerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;

                try
                {
                    pipe = CreatePipeServer(ConduitProjectIdentity.GetPipeName());
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    _ = RunClientLoopAsync(pipe, cancellationToken);
                    pipe = null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException exception) when (!cancellationToken.IsCancellationRequested && !IsShuttingDown())
                {
                    ConduitDiagnostics.Warn($"Unity MCP pipe server could not acquire the project pipe: {exception.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (Exception exception) when (!IsShuttingDown())
                {
                    ConduitDiagnostics.Error("Unity MCP pipe server hit an exception.", exception);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                finally
                {
                    DisposePipe(pipe);
                }
            }
        }

        static async Task RunClientLoopAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
        {
            StreamReader? reader = null;
            ClientSession? session = null;

            try
            {
                reader = new(pipe, utf8NoBom, false, 1024, true);
                if (!await TryHandshakeAsync(pipe, reader, cancellationToken))
                    return;

                session = new()
                {
                    id = CreateClientId(),
                    pipe = pipe,
                    reader = reader,
                };
                _ = RunWriteLoopAsync(session);
                RegisterConnection(session);
                await ConduitToolRunner.OnConnectedAsync();
                await ReadLoopAsync(session, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException exception) when (!cancellationToken.IsCancellationRequested && !IsShuttingDown())
            {
                ConduitDiagnostics.Warn($"Unity MCP pipe server client loop failed: {exception.Message}");
            }
            catch (Exception exception) when (!IsShuttingDown())
            {
                ConduitDiagnostics.Error("Unity MCP pipe server client loop hit an exception.", exception);
            }
            finally
            {
                if (session != null)
                    ClearConnection(session);
                else
                {
                    try
                    {
                        reader?.Dispose();
                    }
                    catch (Exception) { }

                    DisposePipe(pipe);
                }
            }
        }

        static NamedPipeServerStream CreatePipeServer(string pipeName)
            => new(pipeName, PipeDirection.InOut, MaxConcurrentClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        static async Task<bool> TryHandshakeAsync(NamedPipeServerStream pipe, StreamReader reader, CancellationToken cancellationToken)
        {
            var payload = await ReadLineAsync(reader, cancellationToken);
            var message = BridgeProtocol.Deserialize(payload ?? string.Empty);
            if (message?.message_type != BridgeMessageTypes.Hello || message.project == null)
            {
                ConduitDiagnostics.Warn("Rejected MCP client because the first message was not a valid hello envelope.");
                return false;
            }

            var expectedProjectPath = ConduitProjectIdentity.GetProjectPath();
            var requestedProjectPath = ConduitProjectIdentity.NormalizeProjectPath(message.project.project_path);
            if (!string.Equals(requestedProjectPath, expectedProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                ConduitDiagnostics.Warn($"Rejected MCP client for '{requestedProjectPath}' because this editor hosts '{expectedProjectPath}'.");
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            WritePayload(pipe, BridgeProtocol.Serialize(BridgeMessage.CreateHello(CreateHandshake(expectedProjectPath))));
            return true;
        }

        static async Task ReadLoopAsync(ClientSession session, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && session.pipe.IsConnected)
            {
                var payload = await ReadIncomingPayloadAsync(session, cancellationToken);
                if (payload is null)
                    break;

                var message = BridgeProtocol.Deserialize(payload);
                if (message?.message_type == BridgeMessageTypes.Command && message.command != null && message.request_id is { Length: > 0 })
                {
                    inboundMessages.Enqueue(new() { client_id = session.id, message = message });
                    continue;
                }

                ConduitDiagnostics.Warn("Received malformed or unsupported bridge message from the MCP client.");
            }
        }

        static async Task<bool> TrySendMessageAsync(int clientId, BridgeMessage message, CancellationToken cancellationToken, string? commandType)
        {
            if (!clientSessions.TryGetValue(clientId, out var session))
                return false;

            commandType ??= ConduitToolRunner.GetActiveCommandType();
            var context = BuildMessageContext(clientId, message, commandType);
            var outboundMessage = new OutboundClientMessage(BridgeProtocol.Serialize(message), context);
            if (!TryQueueMessage(session, outboundMessage))
                return false;

            try
            {
                return await WaitForSendCompletionAsync(outboundMessage.Completion.Task, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ConduitDiagnostics.Warn($"Timed out while sending {context}. Closing the current pipe connection.");
                ClearConnection(session);
                return false;
            }
        }

        static string BuildMessageContext(int clientId, BridgeMessage message, string? commandType)
            => $"bridge message '{message.message_type}' for request '{message.request_id ?? string.Empty}' ({commandType ?? "unknown_command"}) on pid {Process.GetCurrentProcess().Id}, session {sessionInstanceId}, client {clientId}";

        static void RegisterConnection(ClientSession session)
        {
            clientSessions[session.id] = session;
            UpdateConnectionStatus(ConduitConnectionStatus.Connected);
        }

        static void ClearConnection(ClientSession session)
        {
            var removed = clientSessions.TryRemove(session.id, out _);
            DisposeSession(session);

            if (removed)
            {
                if (clientSessions.IsEmpty)
                    UpdateConnectionStatus(ConduitConnectionStatus.Disconnected);

                if (!IsShuttingDown())
                    ConduitToolRunner.HandleClientDisconnected(session.id);
            }
        }

        static void OnEditorUpdate()
        {
#if UNITY_6000_3_OR_NEWER
            bool refreshToolbar;
            lock (gate)
            {
                if (!toolbarRefreshRequested
                    && status == ConduitConnectionStatus.Disconnected
                    && attachedUntilUtc != default
                    && DateTimeOffset.UtcNow >= attachedUntilUtc)
                {
                    attachedUntilUtc = default;
                    toolbarRefreshRequested = true;
                }

                refreshToolbar = toolbarRefreshRequested;
                toolbarRefreshRequested = false;
            }

            if (refreshToolbar)
                ConduitToolbar.Refresh();
#endif

            while (inboundMessages.TryDequeue(out var inboundMessage))
                ConduitToolRunner.HandleIncomingCommand(inboundMessage.client_id, inboundMessage.message);

            ConduitToolRunner.PumpQueuedCommands();
        }

        static void OnBeforeAssemblyReload() => Stop("Assembly reload starting");

        static void OnEditorQuitting() => Stop("Editor quitting");

        static void UpdateConnectionStatus(ConduitConnectionStatus status)
        {
            lock (gate)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var previousDisplayStatus = GetDisplayStatus(nowUtc);
                ConduitConnection.status = status;
                if (status == ConduitConnectionStatus.Connected)
                    attachedUntilUtc = nowUtc + recentAttachmentCooldown;

                if (previousDisplayStatus != GetDisplayStatus(nowUtc))
                    toolbarRefreshRequested = true;
            }
        }

        static ConduitConnectionStatus GetDisplayStatus(DateTimeOffset nowUtc)
            => status == ConduitConnectionStatus.Connected || nowUtc < attachedUntilUtc
                ? ConduitConnectionStatus.Connected
                : ConduitConnectionStatus.Disconnected;

        static BridgeProjectHandshake CreateHandshake(string projectPath)
        {
            return new()
            {
                project_path = projectPath,
                display_name = Path.GetFileName(projectPath),
                unity_version = Application.unityVersion,
                editor_process_id = Process.GetCurrentProcess().Id,
                session_instance_id = sessionInstanceId,
                last_seen_utc = DateTimeOffset.UtcNow.ToString("O"),
            };
        }

        static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var readTask = reader.ReadLineAsync();
            var completedTask = await Task.WhenAny(readTask, Task.Delay(sendTimeout, cancellationToken));
            if (completedTask != readTask)
                throw new OperationCanceledException(cancellationToken);

            return await readTask;
        }

        static async Task<string?> ReadIncomingPayloadAsync(ClientSession session, CancellationToken cancellationToken)
        {
            var readTask = session.reader.ReadLineAsync();
            if (ConduitToolRunner.HasOutstandingClientWork(session.id)
                || ConduitToolRunner.HasReconnectableWorkForAnyClient())
                return await readTask;

            var completedTask = await Task.WhenAny(readTask, Task.Delay(idleReceiveTimeout, cancellationToken));
            if (completedTask == readTask)
                return await readTask;

            ConduitDiagnostics.Warn($"Closing idle Unity MCP pipe connection after {idleReceiveTimeout.TotalSeconds:0} seconds without incoming messages.");

            try
            {
                session.reader.Dispose();
            }
            catch (Exception) { }

            DisposePipe(session.pipe);

            try
            {
                await readTask;
            }
            catch (Exception) { }

            return null;
        }

        static bool IsShuttingDown()
        {
            lock (gate)
                return shuttingDown;
        }

        static int CreateClientId() => Interlocked.Increment(ref nextClientId);

        static void Stop(string reason)
        {
            CancellationTokenSource? cancellationTokenSource;
            List<ClientSession>? sessions = null;

            lock (gate)
            {
                if (!started)
                    return;

                started = false;
                shuttingDown = true;
                attachedUntilUtc = default;
                cancellationTokenSource = serverLoopCts;
                serverLoopCts = null;

                if (!clientSessions.IsEmpty)
                {
                    sessions = new(clientSessions.Count);
                    foreach (var session in clientSessions.Values)
                        sessions.Add(session);

                    clientSessions.Clear();
                }
            }

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.quitting -= OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            ConduitDiagnostics.Info($"{reason}; canceling Unity MCP pipe server.");
            UpdateConnectionStatus(ConduitConnectionStatus.Disconnected);

            try
            {
                cancellationTokenSource?.Cancel();
            }
            catch (Exception) { }

            if (sessions == null)
                return;

            foreach (var session in sessions)
                DisposeSession(session);
        }

        static void DisposeSession(ClientSession session)
        {
            if (!session.TryMarkDisposed())
                return;

            try
            {
                session.writer_cts.Cancel();
            }
            catch (Exception) { }

            try
            {
                session.reader.Dispose();
            }
            catch (Exception) { }

            DisposePipe(session.pipe);

            try
            {
                session.writer_cts.Dispose();
            }
            catch (Exception) { }

            try
            {
                session.outbound_signal.Dispose();
            }
            catch (Exception) { }

            FailPendingWrites(session);
        }

        static void DisposePipe(NamedPipeServerStream? pipe)
        {
            try
            {
                pipe?.Dispose();
            }
            catch (Exception) { }
        }

        static void WritePayload(Stream stream, string payload)
        {
            var byteCount = utf8NoBom.GetByteCount(payload);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                stream.Write(buffer, 0, written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            stream.Write(newline, 0, newline.Length);
            stream.Flush();
        }

        static async Task WritePayloadAsync(Stream stream, string payload, CancellationToken cancellationToken)
        {
            var byteCount = utf8NoBom.GetByteCount(payload);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                await stream.WriteAsync(buffer, 0, written, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await stream.WriteAsync(newline, 0, newline.Length, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        static bool TryQueueMessage(ClientSession session, OutboundClientMessage outboundMessage)
        {
            if (session.IsDisposed || session.writer_cts.IsCancellationRequested)
                return false;

            session.outbound_messages.Enqueue(outboundMessage);
            try
            {
                session.outbound_signal.Release();
                return true;
            }
            catch (ObjectDisposedException)
            {
                outboundMessage.TrySetResult(false);
                return false;
            }
        }

        static async Task RunWriteLoopAsync(ClientSession session)
        {
            try
            {
                while (!session.writer_cts.IsCancellationRequested)
                {
                    await session.outbound_signal.WaitAsync(session.writer_cts.Token);
                    while (session.outbound_messages.TryDequeue(out var outboundMessage))
                    {
                        try
                        {
                            await WritePayloadAsync(session.pipe, outboundMessage.Payload, session.writer_cts.Token);
                            outboundMessage.TrySetResult(true);
                        }
                        catch (OperationCanceledException) when (session.writer_cts.IsCancellationRequested) { }
                        catch (Exception exception)
                        {
                            if (!IsShuttingDown())
                                ConduitDiagnostics.Error($"Failed to send {outboundMessage.Context}.", exception);
                        }

                        if (outboundMessage.Completion.Task.IsCompleted)
                            continue;

                        outboundMessage.TrySetResult(false);
                        ClearConnection(session);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (session.writer_cts.IsCancellationRequested) { }
            finally
            {
                FailPendingWrites(session);
            }
        }

        static void FailPendingWrites(ClientSession session)
        {
            while (session.outbound_messages.TryDequeue(out var outboundMessage))
                outboundMessage.TrySetResult(false);
        }

        static async Task<bool> WaitForSendCompletionAsync(Task<bool> completionTask, CancellationToken cancellationToken)
        {
            var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completedTask = await Task.WhenAny(completionTask, cancellationTask);
            if (completedTask != completionTask)
                throw new OperationCanceledException(cancellationToken);

            return await completionTask;
        }

        struct InboundClientMessage
        {
            public int client_id;
            public BridgeMessage message;
        }

        sealed class ClientSession
        {
            public int id;
            public NamedPipeServerStream pipe = null!;
            public StreamReader reader = null!;
            public readonly ConcurrentQueue<OutboundClientMessage> outbound_messages = new();
            public readonly SemaphoreSlim outbound_signal = new(0);
            public readonly CancellationTokenSource writer_cts = new();
            int disposed;

            public bool IsDisposed => Volatile.Read(ref disposed) != 0;

            public bool TryMarkDisposed() => Interlocked.Exchange(ref disposed, 1) == 0;
        }

        sealed class OutboundClientMessage
        {
            public OutboundClientMessage(string payload, string context)
            {
                Payload = payload;
                Context = context;
            }

            public string Payload { get; }

            public string Context { get; }

            public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void TrySetResult(bool delivered) => Completion.TrySetResult(delivered);
        }
    }

    enum ConduitConnectionStatus : byte
    {
        Disconnected,
        Connected,
    }
}
