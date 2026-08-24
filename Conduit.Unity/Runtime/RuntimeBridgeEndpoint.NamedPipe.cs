#nullable enable

using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Conduit.Runtime
{
    sealed partial class RuntimeBridgeEndpoint : IDisposable
    {
        const int MaxConcurrentClients = 254;

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
    }
}

