#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ConduitConnection
    {
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
            while (!cancellationToken.IsCancellationRequested && session.Connection.IsConnected)
            {
                var payload = await ReadIncomingPayloadAsync(session, cancellationToken);
                if (payload is null)
                    break;

                var message = BridgeProtocol.Deserialize(payload);
                if (message?.request_id is { Length: > 0 }
                    && (message.message_type == BridgeMessageTypes.CancelCommand
                        || message.message_type == BridgeMessageTypes.Command && message.command != null))
                {
                    inboundMessages.Enqueue(new(session.ID, message));
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
    }
}
