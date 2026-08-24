#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class BridgeMessage
    {
        public int protocol_version = BridgeContract.Version;
        public string message_type = string.Empty;
        public string? request_id;
        public BridgeProjectHandshake? project;
        public BridgeCommand? command;
        public BridgeCommandResult? result;

        public int ProtocolVersion { get => protocol_version; set => protocol_version = value; }
        public string MessageType { get => message_type; set => message_type = value; }
        public string? RequestId { get => request_id; set => request_id = value; }
        public BridgeProjectHandshake? Project { get => project; set => project = value; }
        public BridgeCommand? Command { get => command; set => command = value; }
        public BridgeCommandResult? Result { get => result; set => result = value; }

        public static BridgeMessage CreateHello(BridgeProjectHandshake project)
            => new() { message_type = BridgeMessageTypes.Hello, project = project };

        public static BridgeMessage CreateCommand(string requestId, BridgeCommand command)
            => new() { message_type = BridgeMessageTypes.Command, request_id = requestId, command = command };

        public static BridgeMessage CreateCommandStarted(string requestId)
            => new() { message_type = BridgeMessageTypes.CommandStarted, request_id = requestId };

        public static BridgeMessage CreateCancelCommand(string requestId)
            => new() { message_type = BridgeMessageTypes.CancelCommand, request_id = requestId };

        public static BridgeMessage CreateCommandResult(string requestId, BridgeCommandResult result)
            => new() { message_type = BridgeMessageTypes.CommandResult, request_id = requestId, result = result };
    }
}
