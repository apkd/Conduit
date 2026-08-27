#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Conduit.Runtime
{
    sealed partial class RuntimeBridgeEndpoint : IDisposable
    {
        async Task RunFifoAcceptLoopAsync(CancellationToken ct)
        {
            var clientsDirectory = Path.Combine(endpointDirectory, "clients");
            using var publicationWatcher = new BridgeFilePublicationWatcher(
                clientsDirectory,
                "request.json"
            );
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var clientDirectory in Directory.EnumerateDirectories(clientsDirectory))
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
                                readFifoSynchronously
                                    ? FileOptions.None
                                    : FileOptions.Asynchronous
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
                            _ = RunClientAsync(
                                new RuntimeDuplexConnection(
                                    input,
                                    output,
                                    static () => true,
                                    readSynchronously: readFifoSynchronously
                                ),
                                ct
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

                    await publicationWatcher.WaitAsync(ct);
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
    }
}

