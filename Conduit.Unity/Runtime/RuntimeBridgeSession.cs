#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Runtime
{
    sealed class RuntimeBridgeSession : IDisposable
    {
        readonly RuntimeDuplexConnection connection;
        readonly string endpointDirectory;
        readonly CancellationToken endpointToken;
        readonly ConcurrentDictionary<string, CancellationTokenSource> requests = new();

        internal RuntimeBridgeSession(
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

        internal int Id { get; }

        internal CancellationToken Begin(string requestId)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(endpointToken);
            requests[requestId] = cancellation;
            return cancellation.Token;
        }

        internal void Complete(string requestId)
        {
            if (!requests.TryRemove(requestId, out var cancellation))
                return;

            cancellation.Dispose();
        }

        internal void Cancel(string requestId)
        {
            if (requests.TryGetValue(requestId, out var cancellation))
                cancellation.Cancel();
        }

        internal Task SendAsync(BridgeMessage message)
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

        internal void ResolveArtifacts(BridgeArtifact[] artifacts)
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
}
