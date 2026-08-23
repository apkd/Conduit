#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class ConduitConnection
    {
        const int MaximumPooledPayloadBufferLength = 1024 * 1024;
        const int MaxConcurrentClients = 254;
        static readonly ConcurrentQueue<InboundClientMessage> inboundMessages = new();
        static readonly ConcurrentDictionary<int, ClientSession> clientSessions = new();
        static readonly UTF8Encoding utf8NoBom = new(false);
        static readonly TimeSpan sendTimeout = TimeSpan.FromSeconds(5);
        static readonly TimeSpan idleReceiveTimeout = TimeSpan.FromSeconds(30);
        static readonly TimeSpan recentAttachmentCooldown = TimeSpan.FromHours(1);
        static readonly object gate = new();
        static CancellationTokenSource? serverLoopCts;
        static string? fifoEndpointDirectory;
        static bool started;
        static bool shuttingDown;
        static volatile bool refreshClientHandshakesRequested;
        static bool toolbarRefreshRequested;
        static volatile ConduitConnectionStatus status;
        static long attachedUntilUtcTicks;
        static int handshakeGeneration;
        static int inboundMessageCount;
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
                return GetDisplayStatus(DateTime.UtcNow.Ticks);
        }

        public static Task<bool> TrySendCommandStartedAsync(
            int clientId,
            string requestId,
            string? commandType = null)
            => TrySendMessageAsync(
                clientId,
                BridgeMessage.CreateCommandStarted(requestId),
                commandType
            );

        public static Task<bool> TrySendResultAsync(
            int clientId,
            string requestId,
            BridgeCommandResult result,
            string? commandType = null)
            => TrySendMessageAsync(
                clientId,
                BridgeMessage.CreateCommandResult(requestId, result),
                commandType
            );

        static async Task RunServerLoopAsync(CancellationToken cancellationToken)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                await RunFifoServerLoopAsync(cancellationToken);
                return;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;

                try
                {
                    pipe = CreatePipeServer(ConduitProjectIdentity.GetPipeName());
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    var connectedPipe = pipe;
                    _ = RunClientLoopAsync(
                        EditorBridgeConnection.FromSingleStream(
                            connectedPipe,
                            () => connectedPipe.IsConnected
                        ),
                        cancellationToken
                    );
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

        static async Task RunFifoServerLoopAsync(CancellationToken cancellationToken)
        {
            var root = GetFifoRoot();
            var endpointDirectory = Path.Combine(
                root,
                "endpoints",
                "editor-" + ConduitProjectIdentity.GetPipeName()
            );
            fifoEndpointDirectory = endpointDirectory;
            TryDeleteDirectory(endpointDirectory);
            var clientsDirectory = Path.Combine(endpointDirectory, "clients");
            Directory.CreateDirectory(clientsDirectory);
            TryRestrictDirectory(endpointDirectory);
            WriteEditorEndpointDescriptor(endpointDirectory);
            using var publicationWatcher = new BridgeFilePublicationWatcher(
                clientsDirectory,
                "request.json"
            );

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var clientDirectory in Directory.EnumerateDirectories(clientsDirectory))
                    {
                        var requestPath = Path.Combine(clientDirectory, "request.json");
                        if (!File.Exists(requestPath))
                            continue;

                        try
                        {
                            File.Move(
                                requestPath,
                                Path.Combine(clientDirectory, "accepted.json")
                            );
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        FileStream? input = null;
                        FileStream? output = null;
                        try
                        {
                            input = new(
                                Path.Combine(clientDirectory, "to-unity.fifo"),
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite,
                                4096,
                                FileOptions.Asynchronous
                            );
                            output = new(
                                Path.Combine(clientDirectory, "from-unity.fifo"),
                                FileMode.Open,
                                FileAccess.Write,
                                FileShare.ReadWrite,
                                4096,
                                FileOptions.Asynchronous
                            );
                            File.WriteAllText(
                                Path.Combine(clientDirectory, "connected"),
                                string.Empty
                            );
                            _ = RunClientLoopAsync(
                                new(input, output, static () => true),
                                cancellationToken
                            );
                            input = null;
                            output = null;
                        }
                        finally
                        {
                            input?.Dispose();
                            output?.Dispose();
                        }
                    }

                    await publicationWatcher.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (!IsShuttingDown())
                {
                    ConduitDiagnostics.Error(
                        "Unity MCP FIFO server hit an exception.",
                        exception
                    );
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }

        static string GetFifoRoot()
        {
            if (Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT") is { Length: > 0 } configured)
                return configured;

            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtimeDirectory)
                return Path.Combine(runtimeDirectory, "conduit", "v1");

            return Path.Combine(Path.GetTempPath(), $"conduit-{getuid()}", "v1");
        }

        static void WriteEditorEndpointDescriptor(string endpointDirectory)
        {
            var descriptor = new EditorEndpointDescriptor
            {
                endpoint_id = Path.GetFileName(endpointDirectory),
                project_path = ConduitProjectIdentity.GetProjectPath(),
                process_id = BridgeStatusUtility.ProcessId,
                session_instance_id = sessionInstanceId,
                unity_version = Application.unityVersion,
                platform = Application.platform.ToString(),
                last_seen_utc = DateTimeOffset.UtcNow.ToString("O"),
            };
            var path = Path.Combine(endpointDirectory, "endpoint.json");
            File.WriteAllText(path, JsonUtility.ToJson(descriptor), utf8NoBom);
        }

        static void TryRestrictDirectory(string path)
        {
            try
            {
                chmod(path, 0x1c0); // 0700
            }
            catch (Exception) { }
        }

        static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception) { }
        }

        [DllImport("libc")]
        static extern uint getuid();

        [DllImport("libc")]
        static extern int chmod(string path, uint mode);

        static async Task RunClientLoopAsync(
            EditorBridgeConnection connection,
            CancellationToken cancellationToken)
        {
            StreamReader? reader = null;
            ClientSession? session = null;

            try
            {
                reader = new(connection.Input, utf8NoBom, false, 1024, true);
                int sessionHandshakeGeneration = Volatile.Read(ref handshakeGeneration);
                if (!await TryHandshakeAsync(connection, reader, cancellationToken))
                    return;

                session = new()
                {
                    id = CreateClientId(),
                    handshake_generation = sessionHandshakeGeneration,
                    connection = connection,
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

                    connection.Dispose();
                }
            }
        }

        static NamedPipeServerStream CreatePipeServer(string pipeName)
            => new(pipeName, PipeDirection.InOut, MaxConcurrentClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        internal static async Task<bool> TryHandshakeAsync(
            EditorBridgeConnection connection,
            StreamReader reader,
            CancellationToken cancellationToken)
        {
            var payload = await ReadLineAsync(reader, cancellationToken);
            var message = BridgeProtocol.Deserialize(payload ?? string.Empty);
            if (message?.message_type != BridgeMessageTypes.Hello
                || message.project == null)
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
            WritePayload(
                connection.Output,
                BridgeProtocol.Serialize(
                    BridgeMessage.CreateHello(CreateHandshake(expectedProjectPath))
                )
            );
            if (message.protocol_version == BridgeProtocol.Version)
                return true;

            ConduitDiagnostics.Warn(
                BridgeContract.FormatProtocolMismatch(
                    message.protocol_version,
                    BridgeProtocol.Version
                )
            );
            return false;
        }

        static async Task ReadLoopAsync(ClientSession session, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && session.connection.IsConnected)
            {
                var payload = await ReadIncomingPayloadAsync(session, cancellationToken);
                if (payload is null)
                    break;

                var message = BridgeProtocol.Deserialize(payload);
                if (message?.request_id is { Length: > 0 }
                    && (message.message_type == BridgeMessageTypes.CancelCommand
                        || message.message_type == BridgeMessageTypes.Command && message.command != null))
                {
                    inboundMessages.Enqueue(new() { client_id = session.id, message = message });
                    Interlocked.Increment(ref inboundMessageCount);
                    continue;
                }

                ConduitDiagnostics.Warn("Received malformed or unsupported bridge message from the MCP client.");
            }
        }

        static async Task<bool> TrySendMessageAsync(int clientId, BridgeMessage message, string? commandType)
        {
            if (!clientSessions.TryGetValue(clientId, out var session))
                return false;

            using var timeoutCts = new CancellationTokenSource(sendTimeout);
            commandType ??= ConduitToolRunner.GetActiveCommandType();
            var outboundMessage = new OutboundClientMessage(
                BridgeProtocol.Serialize(message),
                clientId,
                message.message_type,
                message.request_id,
                commandType
            );
            if (!TryQueueMessage(session, outboundMessage))
                return false;

            try
            {
                using var cancellationRegistration = timeoutCts.Token.Register(
                    static state => ((TaskCompletionSource<bool>)state).TrySetCanceled(),
                    outboundMessage.Completion
                );
                return await outboundMessage.Completion.Task;
            }
            catch (OperationCanceledException)
            {
                ConduitDiagnostics.Warn($"Timed out while sending {outboundMessage.Context}. Closing the current pipe connection.");
                ClearConnection(session);
                return false;
            }
        }

        static string BuildMessageContext(
            int clientId,
            string messageType,
            string? requestId,
            string? commandType)
            => $"bridge message '{messageType}' for request '{requestId ?? string.Empty}' ({commandType ?? "unknown_command"}) on pid {BridgeStatusUtility.ProcessId}, session {sessionInstanceId}, client {clientId}";

        static void RegisterConnection(ClientSession session)
        {
            clientSessions[session.id] = session;
            if (session.handshake_generation != Volatile.Read(ref handshakeGeneration))
                refreshClientHandshakesRequested = true;

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

        internal static void RefreshClientHandshakes()
        {
            Interlocked.Increment(ref handshakeGeneration);
            // close idle clients before another command can use their immutable handshake metadata.
            if (!clientSessions.IsEmpty)
                RefreshIdleClientHandshakes();
        }

        static void RefreshIdleClientHandshakes()
        {
            // handshake metadata is immutable, so active clients reconnect after their work completes
            bool deferred = false;
            int currentGeneration = Volatile.Read(ref handshakeGeneration);
            foreach (var session in clientSessions.Values)
            {
                if (session.handshake_generation == currentGeneration)
                    continue;

                if (ConduitToolRunner.HasOutstandingClientWork(session.id))
                {
                    deferred = true;
                    continue;
                }

                ClearConnection(session);
            }

            refreshClientHandshakesRequested = deferred;
        }

        static void OnEditorUpdate()
        {
#if UNITY_6000_3_OR_NEWER && MODULE_IMGUI && MODULE_UIELEMENTS
            var refreshToolbar = false;
            var attachmentDeadline = Volatile.Read(ref attachedUntilUtcTicks);
            if (Volatile.Read(ref toolbarRefreshRequested)
                || status == ConduitConnectionStatus.Disconnected
                && attachmentDeadline != 0
                && DateTime.UtcNow.Ticks >= attachmentDeadline)
            {
                lock (gate)
                {
                    if (!toolbarRefreshRequested
                        && status == ConduitConnectionStatus.Disconnected
                        && attachedUntilUtcTicks != 0
                        && DateTime.UtcNow.Ticks >= attachedUntilUtcTicks)
                    {
                        Volatile.Write(ref attachedUntilUtcTicks, 0);
                        toolbarRefreshRequested = true;
                    }

                    refreshToolbar = toolbarRefreshRequested;
                    toolbarRefreshRequested = false;
                }
            }

            if (refreshToolbar)
                ConduitToolbar.Refresh();
#endif

            if (Volatile.Read(ref inboundMessageCount) > 0)
                while (inboundMessages.TryDequeue(out var inboundMessage))
                {
                    Interlocked.Decrement(ref inboundMessageCount);
                    ConduitToolRunner.HandleIncomingCommand(
                        inboundMessage.client_id,
                        inboundMessage.message
                    );
                }

            ConduitToolRunner.PumpQueuedCommands();
            if (refreshClientHandshakesRequested)
                RefreshIdleClientHandshakes();
        }

        static void OnBeforeAssemblyReload()
        {
            try
            {
                ConduitToolRunner.PrepareForAssemblyReload();
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Failed to checkpoint the active MCP command before assembly reload.", exception);
            }
            finally
            {
                Stop("Assembly reload starting");
            }
        }

        static void OnEditorQuitting() => Stop("Editor quitting");

        static void UpdateConnectionStatus(ConduitConnectionStatus status)
        {
            lock (gate)
            {
                var nowUtcTicks = DateTime.UtcNow.Ticks;
                var previousDisplayStatus = GetDisplayStatus(nowUtcTicks);
                ConduitConnection.status = status;
                if (status == ConduitConnectionStatus.Connected)
                    Volatile.Write(
                        ref attachedUntilUtcTicks,
                        nowUtcTicks + recentAttachmentCooldown.Ticks
                    );

                if (previousDisplayStatus != GetDisplayStatus(nowUtcTicks))
                    toolbarRefreshRequested = true;
            }
        }

        static ConduitConnectionStatus GetDisplayStatus(long nowUtcTicks)
            => status == ConduitConnectionStatus.Connected
               || nowUtcTicks < Volatile.Read(ref attachedUntilUtcTicks)
                ? ConduitConnectionStatus.Connected
                : ConduitConnectionStatus.Disconnected;

        internal static BridgeProjectHandshake CreateHandshake(string projectPath)
        {
            return new()
            {
                project_path = projectPath,
                display_name = Path.GetFileName(projectPath),
                unity_version = Application.unityVersion,
                editor_process_id = BridgeStatusUtility.ProcessId,
                process_id = BridgeStatusUtility.ProcessId,
                endpoint_kind = "editor",
                platform = Application.platform.ToString(),
                cloud_project_id = CloudProjectSettings.projectId,
                company_name = Application.companyName,
                product_name = Application.productName,
                preserve_snippets = ConduitSnippetStorage.PreserveSnippets,
                editor_log_path = Application.consoleLogPath,
                session_instance_id = sessionInstanceId,
                last_seen_utc = DateTimeOffset.UtcNow.ToString("O"),
            };
        }

        static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readTask = reader.ReadLineAsync();
            var timeoutTask = Task.Delay(sendTimeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(readTask, timeoutTask);
            if (completedTask != readTask)
                throw new OperationCanceledException(cancellationToken);

            timeoutCts.Cancel();
            return await readTask;
        }

        static async Task<string?> ReadIncomingPayloadAsync(ClientSession session, CancellationToken cancellationToken)
        {
            var readTask = session.reader.ReadLineAsync();
            if (ShouldKeepConnectionOpen(session.id))
                return await readTask;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(idleReceiveTimeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(readTask, timeoutTask);
            if (completedTask == readTask)
            {
                timeoutCts.Cancel();
                return await readTask;
            }

            if (ShouldKeepConnectionOpen(session.id))
                return await readTask;

            ConduitDiagnostics.Warn($"Closing idle Unity MCP pipe connection after {idleReceiveTimeout.TotalSeconds:0} seconds without incoming messages.");

            try
            {
                session.reader.Dispose();
            }
            catch (Exception) { }

            session.connection.Dispose();

            try
            {
                await readTask;
            }
            catch (Exception) { }

            return null;
        }

        static bool ShouldKeepConnectionOpen(int clientId) =>
            ConduitToolRunner.HasOutstandingClientWork(clientId)
            || ConduitToolRunner.HasReconnectableWorkForAnyClient();

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
                Volatile.Write(ref attachedUntilUtcTicks, 0);
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

            if (fifoEndpointDirectory is { } endpointDirectory)
            {
                fifoEndpointDirectory = null;
                TryDeleteDirectory(endpointDirectory);
            }

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

            session.connection.Dispose();

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
            var bufferLength = checked(byteCount + 1);
            var buffer = bufferLength <= MaximumPooledPayloadBufferLength
                ? ArrayPool<byte>.Shared.Rent(bufferLength)
                : new byte[bufferLength];
            try
            {
                var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                buffer[written++] = (byte)'\n';
                stream.Write(buffer, 0, written);
            }
            finally
            {
                if (buffer.Length <= MaximumPooledPayloadBufferLength)
                    ArrayPool<byte>.Shared.Return(buffer);
            }

            stream.Flush();
        }

        static async Task WritePayloadAsync(Stream stream, string payload, CancellationToken cancellationToken)
        {
            var byteCount = utf8NoBom.GetByteCount(payload);
            var bufferLength = checked(byteCount + 1);
            var buffer = bufferLength <= MaximumPooledPayloadBufferLength
                ? ArrayPool<byte>.Shared.Rent(bufferLength)
                : new byte[bufferLength];
            try
            {
                var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                buffer[written++] = (byte)'\n';
                await stream.WriteAsync(buffer, 0, written, cancellationToken);
            }
            finally
            {
                if (buffer.Length <= MaximumPooledPayloadBufferLength)
                    ArrayPool<byte>.Shared.Return(buffer);
            }

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
                            await WritePayloadAsync(
                                session.connection.Output,
                                outboundMessage.Payload,
                                session.writer_cts.Token
                            );
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

        struct InboundClientMessage
        {
            public int client_id;
            public BridgeMessage message;
        }

        sealed class ClientSession
        {
            public int id;
            public int handshake_generation;
            public EditorBridgeConnection connection = null!;
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
            readonly int clientId;
            readonly string messageType;
            readonly string? requestId;
            readonly string? commandType;

            public OutboundClientMessage(
                string payload,
                int clientId,
                string messageType,
                string? requestId,
                string? commandType)
            {
                Payload = payload;
                this.clientId = clientId;
                this.messageType = messageType;
                this.requestId = requestId;
                this.commandType = commandType;
            }

            public string Payload { get; }

            public string Context => BuildMessageContext(
                clientId,
                messageType,
                requestId,
                commandType
            );

            public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void TrySetResult(bool delivered) => Completion.TrySetResult(delivered);
        }

        [Serializable]
        sealed class EditorEndpointDescriptor
        {
            public int protocol_version = BridgeProtocol.Version;
            public string endpoint_kind = "editor";
            public string transport = "fifo";
            public string endpoint_id = string.Empty;
            public string project_path = string.Empty;
            public int process_id;
            public string session_instance_id = string.Empty;
            public string unity_version = string.Empty;
            public string platform = string.Empty;
            public string last_seen_utc = string.Empty;
        }
    }

    sealed class EditorBridgeConnection : IDisposable
    {
        readonly Func<bool> isConnected;
        bool disposed;

        public EditorBridgeConnection(
            Stream input,
            Stream output,
            Func<bool> isConnected)
        {
            Input = input;
            Output = output;
            this.isConnected = isConnected;
        }

        public Stream Input { get; }

        public Stream Output { get; }

        public bool IsConnected => !disposed && isConnected();

        public static EditorBridgeConnection FromSingleStream(
            Stream stream,
            Func<bool> isConnected) =>
            new(stream, stream, isConnected);

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            if (!ReferenceEquals(Input, Output))
                Input.Dispose();
            Output.Dispose();
        }
    }

    enum ConduitConnectionStatus : byte
    {
        Disconnected,
        Connected,
    }
}
