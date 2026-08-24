#nullable enable

using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ConduitConnection
    {
        readonly struct InboundClientMessage
        {
            internal InboundClientMessage(int clientId, BridgeMessage message)
            {
                ClientID = clientId;
                Message = message;
            }

            internal int ClientID { get; }
            internal BridgeMessage Message { get; }
        }

        sealed class ClientSession
        {
            internal ClientSession(
                int id,
                int handshakeGeneration,
                EditorBridgeConnection connection,
                StreamReader reader)
            {
                ID = id;
                HandshakeGeneration = handshakeGeneration;
                Connection = connection;
                Reader = reader;
            }

            internal int ID { get; }
            internal int HandshakeGeneration { get; }
            internal EditorBridgeConnection Connection { get; }
            internal StreamReader Reader { get; }
            internal ConcurrentQueue<OutboundClientMessage> OutboundMessages { get; } = new();
            internal SemaphoreSlim OutboundSignal { get; } = new(0);
            internal CancellationTokenSource WriterCancellation { get; } = new();
            int disposed;

            internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

            internal bool TryMarkDisposed() => Interlocked.Exchange(ref disposed, 1) == 0;
        }

        sealed class OutboundClientMessage
        {
            readonly int clientId;
            readonly string messageType;
            readonly string? requestId;
            readonly string? commandType;

            internal OutboundClientMessage(
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

            internal string Payload { get; }

            internal string Context => BuildMessageContext(
                clientId,
                messageType,
                requestId,
                commandType
            );

            internal TaskCompletionSource<bool> Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            internal void TrySetResult(bool delivered) => Completion.TrySetResult(delivered);
        }
    }
}

