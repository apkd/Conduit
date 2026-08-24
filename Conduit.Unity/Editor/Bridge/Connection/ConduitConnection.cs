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
    static partial class ConduitConnection
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

        internal static void EnsureStarted()
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

        internal static Task<bool> TrySendCommandStartedAsync(
            int clientId,
            string requestId,
            string? commandType = null)
            => TrySendMessageAsync(
                clientId,
                BridgeMessage.CreateCommandStarted(requestId),
                commandType
            );

        internal static Task<bool> TrySendResultAsync(
            int clientId,
            string requestId,
            BridgeCommandResult result,
            string? commandType = null)
            => TrySendMessageAsync(
                clientId,
                BridgeMessage.CreateCommandResult(requestId, result),
                commandType
            );

        static void RegisterConnection(ClientSession session)
        {
            clientSessions[session.ID] = session;
            if (session.HandshakeGeneration != Volatile.Read(ref handshakeGeneration))
                refreshClientHandshakesRequested = true;

            UpdateConnectionStatus(ConduitConnectionStatus.Connected);
        }

        static void ClearConnection(ClientSession session)
        {
            var removed = clientSessions.TryRemove(session.ID, out _);
            DisposeSession(session);

            if (removed)
            {
                if (clientSessions.IsEmpty)
                    UpdateConnectionStatus(ConduitConnectionStatus.Disconnected);

                if (!IsShuttingDown())
                    ConduitToolRunner.HandleClientDisconnected(session.ID);
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
                if (session.HandshakeGeneration == currentGeneration)
                    continue;

                if (ConduitToolRunner.HasOutstandingClientWork(session.ID))
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
                        inboundMessage.ClientID,
                        inboundMessage.Message
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
                session.WriterCancellation.Cancel();
            }
            catch (Exception) { }

            try
            {
                session.Reader.Dispose();
            }
            catch (Exception) { }

            session.Connection.Dispose();

            try
            {
                session.WriterCancellation.Dispose();
            }
            catch (Exception) { }

            try
            {
                session.OutboundSignal.Dispose();
            }
            catch (Exception) { }

            FailPendingWrites(session);
        }

    }
}
