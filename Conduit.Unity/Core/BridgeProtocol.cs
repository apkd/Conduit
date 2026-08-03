#nullable enable

using System;
using UnityEngine;

namespace Conduit
{
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
                _                                 => JsonUtility.ToJson(message),
            };

        public static BridgeMessage? Deserialize(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            try
            {
                var message = JsonUtility.FromJson<BridgeMessage>(payload);
                if (message == null)
                    return null;

                switch (message.message_type)
                {
                    case BridgeMessageTypes.Hello:
                        message.request_id = null;
                        message.command = null;
                        message.result = null;
                        return message;
                    case BridgeMessageTypes.Command:
                        message.project = null;
                        message.result = null;
                        return message;
                    case BridgeMessageTypes.CancelCommand:
                    case BridgeMessageTypes.CommandStarted:
                        message.project = null;
                        message.command = null;
                        message.result = null;
                        return message;
                    case BridgeMessageTypes.CommandResult:
                        message.project = null;
                        message.command = null;
                        return message;
                    default:
                        return null;
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
