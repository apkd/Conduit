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

                session = new(
                    CreateClientId(),
                    sessionHandshakeGeneration,
                    connection,
                    reader
                );
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

        static void DisposePipe(NamedPipeServerStream? pipe)
        {
            try
            {
                pipe?.Dispose();
            }
            catch (Exception) { }
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
}
