#nullable enable

using System;
using UnityEngine;

namespace Conduit
{
    static class BridgeCommandTypes
    {
        public const string Status = "status";
        public const string Play = "play";
        public const string Screenshot = "screenshot";
        public const string GetDependencies = "get_dependencies";
        public const string FindReferencesTo = "find_references_to";
        public const string FindMissingScripts = "find_missing_scripts";
        public const string Show = "show";
        public const string Search = "search";
        public const string ToJson = "to_json";
        public const string FromJsonOverwrite = "from_json_overwrite";
        public const string SaveScenes = "save_scenes";
        public const string DiscardScenes = "discard_scenes";
        public const string RefreshAssetDatabase = "refresh_asset_database";
        public const string ExecuteCode = "execute_code";
        public const string RunTestsEditMode = "run_tests_editmode";
        public const string RunTestsPlayMode = "run_tests_playmode";
        public const string RunTestsPlayer = "run_tests_player";
    }

    static class BridgeMessageTypes
    {
        public const string Hello = "hello";
        public const string Command = "command";
        public const string CommandStarted = "command_started";
        public const string CommandResult = "command_result";
    }

    static class ToolOutcome
    {
        public const string Success = "success";
        public const string Exception = "exception";
        public const string CompileError = "compile_error";
        public const string TestFailed = "test_failed";
        public const string Timeout = "timeout";
        public const string NotConnected = "not_connected";
        public const string DirtyScene = "dirty_scene";
        public const string AmbiguousTarget = "ambiguous_target";
        public const string Cancelled = "cancelled";
    }

    [Serializable]
    sealed class BridgeMessage
    {
        public int protocol_version = 2;
        public string message_type = string.Empty;
        public string? request_id;
        public BridgeProjectHandshake? project;
        public BridgeCommand? command;
        public BridgeCommandResult? result;

        public static BridgeMessage CreateHello(BridgeProjectHandshake project)
            => new()
            {
                message_type = BridgeMessageTypes.Hello,
                project = project,
            };

        public static BridgeMessage CreateCommandStarted(string requestId)
            => new()
            {
                message_type = BridgeMessageTypes.CommandStarted,
                request_id = requestId,
            };

        public static BridgeMessage CreateCommandResult(string requestId, BridgeCommandResult result)
            => new()
            {
                message_type = BridgeMessageTypes.CommandResult,
                request_id = requestId,
                result = result,
            };
    }

    [Serializable]
    public sealed class BridgeProjectHandshake
    {
        public string project_path = string.Empty;
        public string display_name = string.Empty;
        public string unity_version = string.Empty;
        public int editor_process_id;
        public string session_instance_id = string.Empty;
        public string last_seen_utc = string.Empty;
    }

    [Serializable]
    sealed class BridgeCommand
    {
        public string command_type = string.Empty;
        public string? target;
        public string? snippet;
        public string? test_filter;
        public bool rebuild_cache;
        public bool is_restored;
        public string[] args = Array.Empty<string>();
    }

    [Serializable]
    sealed class BridgeCommandResult
    {
        public string outcome = ToolOutcome.Success;
        public string logs = string.Empty;
        public string? return_value;
        public BridgeExceptionInfo? exception;
        public string? diagnostic;
    }

    [Serializable]
    sealed class BridgeExceptionInfo
    {
        public string type = string.Empty;
        public string message = string.Empty;
        public string? stack_trace;
    }

    [Serializable]
    sealed class PendingOperationState
    {
        public string request_id = string.Empty;
        public string command_type = string.Empty;
        public string? target;
        public string? snippet;
        public string? test_filter;
        public bool rebuild_cache;
        public bool is_restored;
        public int client_id;
        public bool is_acknowledged;
        public string[] args = Array.Empty<string>();
    }

    [Serializable]
    sealed class ReferenceCacheDocument
    {
        public string cached_at_utc = string.Empty;
        public SerializableLookupEntry[] entries = Array.Empty<SerializableLookupEntry>();
    }

    [Serializable]
    sealed class SerializableLookupEntry
    {
        public string guid = string.Empty;
        public string[] referencer_guids = Array.Empty<string>();
    }

    [Serializable]
    sealed class BridgeMessageHeader
    {
        public string message_type = string.Empty;
    }

    [Serializable]
    sealed class BridgeHelloEnvelope
    {
        public int protocol_version = 2;
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
        public int protocol_version = 2;
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
        public int protocol_version = 2;
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
        public int protocol_version = 2;
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

    static class BridgeProtocol
    {
        public static string Serialize(BridgeMessage message)
            => message.message_type switch
            {
                BridgeMessageTypes.Hello          => JsonUtility.ToJson(new BridgeHelloEnvelope(message)),
                BridgeMessageTypes.Command        => JsonUtility.ToJson(new BridgeCommandEnvelope(message)),
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
