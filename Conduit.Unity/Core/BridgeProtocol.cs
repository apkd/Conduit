#nullable enable

using System;
using UnityEngine;

namespace Conduit
{
    [Serializable]
    sealed class BridgeMessageHeader
    {
        public string message_type = string.Empty;
    }

    [Serializable]
    sealed class BridgeHelloEnvelope
    {
        public int protocol_version = BridgeContract.Version;
        public string message_type = string.Empty;
        public BridgeProjectHandshake? project;

        public BridgeHelloEnvelope() { }

        public BridgeHelloEnvelope(BridgeMessage message)
        {
            protocol_version = message.protocol_version;
            message_type = message.message_type;
            project = message.project;
        }

        public BridgeMessage ToMessage()
            => new()
            {
                protocol_version = protocol_version,
                message_type = message_type,
                project = project,
            };
    }

    [Serializable]
    sealed class BridgeCommandEnvelope
    {
        public int protocol_version = BridgeContract.Version;
        public string message_type = string.Empty;
        public string request_id = string.Empty;
        public BridgeCommand? command;

        public BridgeCommandEnvelope() { }

        public BridgeCommandEnvelope(BridgeMessage message)
        {
            protocol_version = message.protocol_version;
            message_type = message.message_type;
            request_id = message.request_id ?? string.Empty;
            command = message.command;
        }

        public BridgeMessage ToMessage()
            => new()
            {
                protocol_version = protocol_version,
                message_type = message_type,
                request_id = request_id,
                command = command,
            };
    }

    [Serializable]
    sealed class BridgeCommandStartedEnvelope
    {
        public int protocol_version = BridgeContract.Version;
        public string message_type = string.Empty;
        public string request_id = string.Empty;

        public BridgeCommandStartedEnvelope() { }

        public BridgeCommandStartedEnvelope(BridgeMessage message)
        {
            protocol_version = message.protocol_version;
            message_type = message.message_type;
            request_id = message.request_id ?? string.Empty;
        }

        public BridgeMessage ToMessage()
            => new()
            {
                protocol_version = protocol_version,
                message_type = message_type,
                request_id = request_id,
            };
    }

    [Serializable]
    sealed class BridgeCommandResultEnvelope
    {
        public int protocol_version = BridgeContract.Version;
        public string message_type = string.Empty;
        public string request_id = string.Empty;
        public BridgeCommandResult? result;

        public BridgeCommandResultEnvelope() { }

        public BridgeCommandResultEnvelope(BridgeMessage message)
        {
            protocol_version = message.protocol_version;
            message_type = message.message_type;
            request_id = message.request_id ?? string.Empty;
            result = message.result;
        }

        public BridgeMessage ToMessage()
            => new()
            {
                protocol_version = protocol_version,
                message_type = message_type,
                request_id = request_id,
                result = result,
            };
    }

    /// <summary>Serializes bridge envelopes with Unity's field-based JSON serializer.</summary>
    static class BridgeProtocol
    {
        public const int Version = BridgeContract.Version;

        public static string Serialize(BridgeMessage message)
            => message.message_type switch
            {
                BridgeMessageTypes.Hello          => JsonUtility.ToJson(new BridgeHelloEnvelope(message)),
                BridgeMessageTypes.Command        => JsonUtility.ToJson(new BridgeCommandEnvelope(message)),
                BridgeMessageTypes.CancelCommand  => JsonUtility.ToJson(new BridgeCommandStartedEnvelope(message)),
                BridgeMessageTypes.CommandStarted => JsonUtility.ToJson(new BridgeCommandStartedEnvelope(message)),
                BridgeMessageTypes.CommandResult  => JsonUtility.ToJson(new BridgeCommandResultEnvelope(message)),
                _                                 => JsonUtility.ToJson(new BridgeMessageHeader { message_type = message.message_type }),
            };

        public static BridgeMessage? Deserialize(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            try
            {
                var header = JsonUtility.FromJson<BridgeMessageHeader>(payload);
                return header?.message_type switch
                {
                    BridgeMessageTypes.Hello          => JsonUtility.FromJson<BridgeHelloEnvelope>(payload)?.ToMessage(),
                    BridgeMessageTypes.Command        => JsonUtility.FromJson<BridgeCommandEnvelope>(payload)?.ToMessage(),
                    BridgeMessageTypes.CancelCommand  => JsonUtility.FromJson<BridgeCommandStartedEnvelope>(payload)?.ToMessage(),
                    BridgeMessageTypes.CommandStarted => JsonUtility.FromJson<BridgeCommandStartedEnvelope>(payload)?.ToMessage(),
                    BridgeMessageTypes.CommandResult  => JsonUtility.FromJson<BridgeCommandResultEnvelope>(payload)?.ToMessage(),
                    _                                 => null,
                };
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
