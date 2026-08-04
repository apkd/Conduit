#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Process = System.Diagnostics.Process;

namespace Conduit.Runtime
{
    sealed class RuntimeBridgeEndpoint : IDisposable
    {
        const int MaxConcurrentClients = 254;
        static readonly UTF8Encoding utf8NoBom = new(false);
        static readonly TimeSpan leaseInterval = TimeSpan.FromSeconds(2);
        readonly CancellationTokenSource cancellation = new();
        readonly ConcurrentDictionary<int, RuntimeBridgeSession> sessions = new();
        readonly BridgeEndpointDescriptor descriptor;
        readonly BridgeProjectHandshake handshake;
        readonly string endpointDirectory;
        readonly bool useFifo;
        int nextSessionId;

        public RuntimeBridgeEndpoint()
        {
            var processId = Process.GetCurrentProcess().Id;
            var sessionId = Guid.NewGuid().ToString("N");
            var wine = RuntimePlatformUtility.IsWine();
            useFifo = Application.platform != RuntimePlatform.WindowsPlayer || wine;
            var root = RuntimeIpcPaths.GetRoot(wine);
            endpointDirectory = Path.Combine(root, "endpoints", $"player-{processId}-{sessionId}");
            Directory.CreateDirectory(Path.Combine(endpointDirectory, "clients"));
            RuntimeIpcPaths.TryRestrictDirectory(endpointDirectory);

            handshake = new()
            {
                display_name = Application.productName,
                unity_version = Application.unityVersion,
                process_id = processId,
                endpoint_kind = "player",
                platform = Application.platform.ToString(),
                build_guid = Application.buildGUID,
                cloud_project_id = Application.cloudProjectId,
                company_name = Application.companyName,
                product_name = Application.productName,
                can_monitor_process = !wine,
                session_instance_id = sessionId,
                handoff_token = Environment.GetEnvironmentVariable("CONDUIT_HANDOFF_TOKEN") ?? string.Empty,
                last_seen_utc = DateTimeOffset.UtcNow.ToString("O"),
            };
            descriptor = new()
            {
                endpoint_kind = "player",
                transport = useFifo ? "fifo" : "named_pipe",
                endpoint_id = Path.GetFileName(endpointDirectory),
                pipe_name = useFifo ? string.Empty : $"unity-conduit-player-{processId}-{sessionId[..12]}",
                process_id = processId,
                session_instance_id = sessionId,
                handoff_token = Environment.GetEnvironmentVariable("CONDUIT_HANDOFF_TOKEN") ?? string.Empty,
                unity_version = Application.unityVersion,
                platform = Application.platform.ToString(),
                build_guid = Application.buildGUID,
                cloud_project_id = Application.cloudProjectId,
                company_name = Application.companyName,
                product_name = Application.productName,
                started_utc = DateTimeOffset.UtcNow.ToString("O"),
                last_seen_utc = DateTimeOffset.UtcNow.ToString("O"),
                can_monitor_process = !wine,
                is_test_player = Environment.GetEnvironmentVariable("CONDUIT_TEST_PLAYER") == "1",
            };
        }

        public void Start()
        {
            WriteDescriptor();
            _ = RefreshLeaseAsync(cancellation.Token);
            _ = useFifo
                ? RunFifoAcceptLoopAsync(cancellation.Token)
                : RunNamedPipeAcceptLoopAsync(cancellation.Token);
        }

        public string SessionInstanceId => descriptor.session_instance_id;

        public void Dispose()
        {
            cancellation.Cancel();
            foreach (var session in sessions.Values)
                session.Dispose();
            sessions.Clear();
            cancellation.Dispose();

            try
            {
                Directory.Delete(endpointDirectory, recursive: true);
            }
            catch (Exception) { }
        }

        async Task RefreshLeaseAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(leaseInterval, ct).ConfigureAwait(false);
                    descriptor.last_seen_utc = DateTimeOffset.UtcNow.ToString("O");
                    WriteDescriptor();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Conduit player endpoint lease update failed: {exception.Message}");
                }
            }
        }

        async Task RunNamedPipeAcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = new(
                        descriptor.pipe_name,
                        PipeDirection.InOut,
                        MaxConcurrentClients,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );
                    await pipe.WaitForConnectionAsync(ct);
                    var connectedPipe = pipe;
                    var connection = RuntimeDuplexConnection.FromSingleStream(
                        connectedPipe,
                        () => connectedPipe.IsConnected
                    );
                    pipe = null;
                    _ = RunClientAsync(connection, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    pipe?.Dispose();
                    Debug.LogWarning($"Conduit player named-pipe accept failed: {exception.Message}");
                    await DelayAfterFailureAsync(ct);
                }
            }
        }

        async Task RunFifoAcceptLoopAsync(CancellationToken ct)
        {
            var clientsDirectory = Path.Combine(endpointDirectory, "clients");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var clientDirectory in Directory.GetDirectories(clientsDirectory))
                    {
                        var publicationPath = Path.Combine(clientDirectory, "request.json");
                        var acceptedPath = Path.Combine(clientDirectory, "accepted.json");
                        if (!File.Exists(publicationPath))
                            continue;

                        try
                        {
                            File.Move(publicationPath, acceptedPath);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        var input = new FileStream(
                            Path.Combine(clientDirectory, "to-unity.fifo"),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite,
                            4096,
                            FileOptions.Asynchronous
                        );
                        var output = new FileStream(
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
                        _ = RunClientAsync(
                            new RuntimeDuplexConnection(input, output, static () => true),
                            ct
                        );
                    }

                    await Task.Delay(50, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Conduit player FIFO accept failed: {exception.Message}");
                    await DelayAfterFailureAsync(ct);
                }
            }
        }

        async Task RunClientAsync(RuntimeDuplexConnection connection, CancellationToken endpointToken)
        {
            RuntimeBridgeSession? session = null;
            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(endpointToken);
                handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                var payload = await connection.Reader.ReadLineAsync();
                handshakeCts.Token.ThrowIfCancellationRequested();
                var request = BridgeProtocol.Deserialize(payload ?? string.Empty);
                if (request?.message_type != BridgeMessageTypes.Hello
                    || request.project == null
                    || request.protocol_version != BridgeProtocol.Version
                    || request.project.process_id is > 0 && request.project.process_id != descriptor.process_id
                    || request.project.session_instance_id is { Length: > 0 }
                    && request.project.session_instance_id != descriptor.session_instance_id)
                    return;

                handshake.last_seen_utc = DateTimeOffset.UtcNow.ToString("O");
                await connection.WriteAsync(
                    BridgeProtocol.Serialize(BridgeMessage.CreateHello(handshake)),
                    endpointToken
                );

                session = new(
                    Interlocked.Increment(ref nextSessionId),
                    connection,
                    endpointDirectory,
                    endpointToken
                );
                sessions[session.Id] = session;
                while (!endpointToken.IsCancellationRequested && connection.IsConnected)
                {
                    payload = await connection.Reader.ReadLineAsync();
                    if (payload == null)
                        break;

                    var message = BridgeProtocol.Deserialize(payload);
                    if (message?.request_id is not { Length: > 0 })
                        continue;

                    if (message.message_type == BridgeMessageTypes.CancelCommand)
                    {
                        session.Cancel(message.request_id);
                        continue;
                    }

                    if (message.message_type != BridgeMessageTypes.Command || message.command == null)
                        continue;

                    await session.SendAsync(BridgeMessage.CreateCommandStarted(message.request_id));
                    try
                    {
                        session.ResolveArtifacts(message.command.artifacts);
                    }
                    catch (Exception exception)
                    {
                        await session.SendAsync(
                            BridgeMessage.CreateCommandResult(
                                message.request_id,
                                BridgeCommandResult.FromException(exception)
                            )
                        );
                        continue;
                    }

                    RuntimeBridgeDispatcher.Enqueue(session, message.request_id, message.command);
                }
            }
            catch (OperationCanceledException) when (endpointToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Debug.LogWarning($"Conduit player client disconnected: {exception.Message}");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (session != null)
                    sessions.TryRemove(session.Id, out _);
                connection.Dispose();
            }
        }

        void WriteDescriptor()
        {
            var path = Path.Combine(endpointDirectory, "endpoint.json");
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonUtility.ToJson(descriptor), utf8NoBom);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
        }

        static async Task DelayAfterFailureAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException) { }
        }
    }

    sealed class RuntimeDuplexConnection : IDisposable
    {
        static readonly UTF8Encoding utf8NoBom = new(false);
        readonly Stream input;
        readonly Stream output;
        readonly Func<bool> isConnected;
        readonly SemaphoreSlim writeGate = new(1, 1);
        bool disposed;

        public RuntimeDuplexConnection(Stream input, Stream output, Func<bool> isConnected)
        {
            this.input = input;
            this.output = output;
            this.isConnected = isConnected;
            Reader = new(input, utf8NoBom, false, 1024, true);
        }

        public StreamReader Reader { get; }

        public bool IsConnected => !disposed && isConnected();

        public static RuntimeDuplexConnection FromSingleStream(Stream stream, Func<bool> connected)
            => new(stream, stream, connected);

        public async Task WriteAsync(string payload, CancellationToken ct)
        {
            await writeGate.WaitAsync(ct);
            try
            {
                var bytes = utf8NoBom.GetBytes(payload + "\n");
                await output.WriteAsync(bytes, 0, bytes.Length, ct);
                await output.FlushAsync(ct);
            }
            finally
            {
                writeGate.Release();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Reader.Dispose();
            if (!ReferenceEquals(input, output))
                input.Dispose();
            output.Dispose();
            writeGate.Dispose();
        }
    }

    sealed class RuntimeBridgeSession : IDisposable
    {
        readonly RuntimeDuplexConnection connection;
        readonly string endpointDirectory;
        readonly CancellationToken endpointToken;
        readonly ConcurrentDictionary<string, CancellationTokenSource> requests = new();

        public RuntimeBridgeSession(
            int id,
            RuntimeDuplexConnection connection,
            string endpointDirectory,
            CancellationToken endpointToken)
        {
            Id = id;
            this.connection = connection;
            this.endpointDirectory = endpointDirectory;
            this.endpointToken = endpointToken;
        }

        public int Id { get; }

        public CancellationToken Begin(string requestId)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(endpointToken);
            requests[requestId] = cancellation;
            return cancellation.Token;
        }

        public void Complete(string requestId)
        {
            if (!requests.TryRemove(requestId, out var cancellation))
                return;

            cancellation.Dispose();
        }

        public void Cancel(string requestId)
        {
            if (requests.TryGetValue(requestId, out var cancellation))
                cancellation.Cancel();
        }

        public Task SendAsync(BridgeMessage message)
        {
            if (message.result?.artifacts is { } artifacts)
            {
                try
                {
                    foreach (var artifact in artifacts)
                        if (artifact.Content != null)
                            artifact.MaterializeInEndpoint(endpointDirectory);
                        else
                            artifact.ResolveInEndpoint(endpointDirectory);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException
                        or ArgumentException
                        or NotSupportedException)
                {
                    message = BridgeMessage.CreateCommandResult(
                        message.request_id!,
                        BridgeCommandResult.FromException(exception)
                    );
                }
            }

            return connection.WriteAsync(
                BridgeProtocol.Serialize(message),
                endpointToken
            );
        }

        public void ResolveArtifacts(BridgeArtifact[] artifacts)
        {
            foreach (var artifact in artifacts)
                artifact.ResolveInEndpoint(endpointDirectory);
        }

        public void Dispose()
        {
            foreach (var cancellation in requests.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }

            requests.Clear();
        }
    }

    static class RuntimePlatformUtility
    {
        public static bool IsWine()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer)
                return false;

            try
            {
                return wine_get_version() != IntPtr.Zero;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("ntdll.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr wine_get_version();
    }

    static class RuntimeIpcPaths
    {
        public static string GetRoot(bool wine)
        {
            if (Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT") is { Length: > 0 } configured)
                return wine ? ToWinePath(configured) : configured;

            if (wine)
            {
                var home = Environment.GetEnvironmentVariable("WINE_HOST_HOME")
                           ?? Environment.GetEnvironmentVariable("HOME")
                           ?? string.Empty;
                if (home.Length == 0)
                    throw new InvalidOperationException(
                        "Conduit could not resolve the Wine host home directory. Set CONDUIT_IPC_ROOT."
                    );

                return Path.Combine(ToWinePath(home), ".local", "state", "conduit", "ipc", "v1");
            }

            if (Application.platform == RuntimePlatform.WindowsPlayer)
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Conduit",
                    "ipc",
                    "v1"
                );

            if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtimeDirectory)
                return Path.Combine(runtimeDirectory, "conduit", "v1");

            return Path.Combine(Path.GetTempPath(), $"conduit-{getuid()}", "v1");
        }

        public static void TryRestrictDirectory(string path)
        {
            if (Application.platform == RuntimePlatform.WindowsPlayer)
                return;

            try
            {
                chmod(path, 0x1c0); // 0700
            }
            catch (Exception) { }
        }

        static string ToWinePath(string path)
        {
            if (path.Length >= 2 && path[1] == ':')
                return path;

            var fallback = "Z:" + path.Replace('/', '\\');
            var converted = IntPtr.Zero;
            try
            {
                // respect custom Wine drive mappings instead of assuming that Z: exposes the host root
                converted = wine_get_dos_file_name(path);
                return converted == IntPtr.Zero
                    ? fallback
                    : Marshal.PtrToStringUni(converted) ?? fallback;
            }
            catch (Exception exception) when (
                exception is DllNotFoundException or EntryPointNotFoundException
            )
            {
                return fallback;
            }
            finally
            {
                if (converted != IntPtr.Zero)
                    HeapFree(GetProcessHeap(), 0, converted); // allocated on Wine's process heap
            }
        }

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr wine_get_dos_file_name(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string unixPath
        );

        [DllImport("kernel32.dll")]
        static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool HeapFree(IntPtr heap, uint flags, IntPtr memory);

        [DllImport("libc")]
        static extern uint getuid();

        [DllImport("libc")]
        static extern int chmod(string path, uint mode);
    }
}
