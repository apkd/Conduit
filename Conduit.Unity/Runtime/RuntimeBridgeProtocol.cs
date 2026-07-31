#nullable enable

using System;
using System.IO;
using UnityEngine;

namespace Conduit.Runtime
{
    static class RuntimeBridgeProtocol
    {
        public const int Version = 4;

        public static string Serialize(RuntimeBridgeMessage message)
            => message.message_type switch
            {
                RuntimeBridgeMessageTypes.Hello          => JsonUtility.ToJson(new RuntimeHelloEnvelope(message)),
                RuntimeBridgeMessageTypes.CommandStarted => JsonUtility.ToJson(new RuntimeCommandStartedEnvelope(message)),
                RuntimeBridgeMessageTypes.CommandResult  => JsonUtility.ToJson(new RuntimeCommandResultEnvelope(message)),
                _ => JsonUtility.ToJson(message),
            };

        public static RuntimeBridgeMessage? Deserialize(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
                return null;

            try
            {
                var header = JsonUtility.FromJson<RuntimeMessageHeader>(payload);
                return header?.message_type switch
                {
                    RuntimeBridgeMessageTypes.Hello => JsonUtility.FromJson<RuntimeHelloEnvelope>(payload)?.ToMessage(),
                    RuntimeBridgeMessageTypes.Command => JsonUtility.FromJson<RuntimeCommandEnvelope>(payload)?.ToMessage(),
                    RuntimeBridgeMessageTypes.CancelCommand => JsonUtility.FromJson<RuntimeCommandStartedEnvelope>(payload)?.ToMessage(),
                    _ => null,
                };
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    static class RuntimeBridgeMessageTypes
    {
        public const string Hello = "hello";
        public const string Command = "command";
        public const string CancelCommand = "cancel_command";
        public const string CommandStarted = "command_started";
        public const string CommandResult = "command_result";
    }

    static class RuntimeBridgeCommandTypes
    {
        public const string Help = "help";
        public const string Restart = "restart";
        public const string Status = "status";
        public const string Screenshot = "screenshot";
        public const string Show = "show";
        public const string Search = "search";
        public const string ToJson = "to_json";
        public const string FromJsonOverwrite = "from_json_overwrite";
        public const string ExecuteCode = "execute_code";
        public const string Reflect = "reflect";
        public const string ProfilerRecord = "profiler_record";
        public const string ProfilerOverview = "profiler_overview";
        public const string ProfilerBrowse = "profiler_browse";
        public const string CompilationReferences = "compilation_references";
        public const string AssemblyBlob = "assembly_blob";
    }

    static class RuntimeToolOutcome
    {
        public const string Success = "success";
        public const string Exception = "exception";
        public const string CompileError = "compile_error";
        public const string Cancelled = "cancelled";
        public const string AmbiguousTarget = "ambiguous_target";
    }

    [Serializable]
    sealed class RuntimeBridgeMessage
    {
        public int protocol_version = RuntimeBridgeProtocol.Version;
        public string message_type = string.Empty;
        public string? request_id;
        public RuntimeBridgeHandshake? project;
        public RuntimeBridgeCommand? command;
        public RuntimeBridgeCommandResult? result;

        public static RuntimeBridgeMessage Hello(RuntimeBridgeHandshake handshake)
            => new() { message_type = RuntimeBridgeMessageTypes.Hello, project = handshake };

        public static RuntimeBridgeMessage Started(string requestId)
            => new() { message_type = RuntimeBridgeMessageTypes.CommandStarted, request_id = requestId };

        public static RuntimeBridgeMessage Result(string requestId, RuntimeBridgeCommandResult result)
            => new()
            {
                message_type = RuntimeBridgeMessageTypes.CommandResult,
                request_id = requestId,
                result = result,
            };
    }

    [Serializable]
    sealed class RuntimeBridgeHandshake
    {
        public string project_path = string.Empty;
        public string display_name = string.Empty;
        public string unity_version = string.Empty;
        public int editor_process_id;
        public int process_id;
        public string endpoint_kind = "player";
        public string platform = string.Empty;
        public string build_guid = string.Empty;
        public string cloud_project_id = string.Empty;
        public string company_name = string.Empty;
        public string product_name = string.Empty;
        public bool can_monitor_process;
        public string[] capabilities = Array.Empty<string>();
        public string editor_log_path = string.Empty;
        public string session_instance_id = string.Empty;
        public string handoff_token = string.Empty;
        public string last_seen_utc = string.Empty;
    }

    [Serializable]
    sealed class RuntimeBridgeCommand
    {
        public string command_type = string.Empty;
        public string? target;
        public string? snippet;
        public string? display_name;
        public string? test_filter;
        public bool @async;
        public bool rebuild_cache;
        public bool track_usage;
        public string[] args = Array.Empty<string>();
        public RuntimeBridgeArtifact[] artifacts = Array.Empty<RuntimeBridgeArtifact>();
    }

    [Serializable]
    sealed class RuntimeBridgeCommandResult
    {
        public string outcome = RuntimeToolOutcome.Success;
        public string logs = string.Empty;
        public string? display_name;
        public string? return_value;
        public RuntimeBridgeException? exception;
        public string? diagnostic;
        public RuntimeBridgeArtifact[] artifacts = Array.Empty<RuntimeBridgeArtifact>();

        public static RuntimeBridgeCommandResult Success(string? value = null)
            => new() { return_value = value };

        public static RuntimeBridgeCommandResult EditorOnly(string commandName)
            => new()
            {
                outcome = RuntimeToolOutcome.Exception,
                diagnostic = $"The tool `{commandName}` is editor-only.",
            };

        public static RuntimeBridgeCommandResult FromException(Exception exception)
            => new()
            {
                outcome = RuntimeToolOutcome.Exception,
                exception = new()
                {
                    type = exception.GetType().FullName ?? exception.GetType().Name,
                    message = exception.Message,
                    stack_trace = exception.StackTrace,
                },
            };
    }

    [Serializable]
    sealed class RuntimeBridgeException
    {
        public string type = string.Empty;
        public string message = string.Empty;
        public string? stack_trace;
    }

    [Serializable]
    sealed class RuntimeBridgeArtifact
    {
        const int EncodedChunkSize = 48 * 1024;

        public string name = string.Empty;
        public string media_type = "application/octet-stream";
        public string sha256 = string.Empty;
        public string? relative_path;
        public string[] chunks = Array.Empty<string>();

        public static RuntimeBridgeArtifact FromBytes(string name, string mediaType, byte[] bytes)
        {
            var encoded = Convert.ToBase64String(bytes);
            var chunks = new string[Math.Max(1, (encoded.Length + EncodedChunkSize - 1) / EncodedChunkSize)];
            for (var index = 0; index < chunks.Length; index++)
            {
                var start = index * EncodedChunkSize;
                chunks[index] = encoded.Substring(start, Math.Min(EncodedChunkSize, encoded.Length - start));
            }

            return new()
            {
                name = name,
                media_type = mediaType,
                sha256 = RuntimeHash.Sha256(bytes),
                chunks = chunks,
            };
        }

        public byte[] Decode()
        {
            var bytes = string.IsNullOrWhiteSpace(relative_path)
                ? Convert.FromBase64String(string.Concat(chunks))
                : File.ReadAllBytes(RuntimeIpcPaths.ResolveRelativePath(relative_path));
            if (!string.Equals(RuntimeHash.Sha256(bytes), sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Artifact '{name}' failed SHA-256 verification.");

            return bytes;
        }
    }

    [Serializable]
    sealed class RuntimeMessageHeader
    {
        public string message_type = string.Empty;
    }

    [Serializable]
    sealed class RuntimeHelloEnvelope
    {
        public int protocol_version = RuntimeBridgeProtocol.Version;
        public string message_type = string.Empty;
        public RuntimeBridgeHandshake? project;

        public RuntimeHelloEnvelope() { }

        public RuntimeHelloEnvelope(RuntimeBridgeMessage message)
        {
            message_type = message.message_type;
            project = message.project;
        }

        public RuntimeBridgeMessage ToMessage()
            => new() { protocol_version = protocol_version, message_type = message_type, project = project };
    }

    [Serializable]
    sealed class RuntimeCommandEnvelope
    {
        public int protocol_version = RuntimeBridgeProtocol.Version;
        public string message_type = string.Empty;
        public string request_id = string.Empty;
        public RuntimeBridgeCommand? command;

        public RuntimeCommandEnvelope() { }

        public RuntimeBridgeMessage ToMessage()
            => new()
            {
                protocol_version = protocol_version,
                message_type = message_type,
                request_id = request_id,
                command = command,
            };
    }

    [Serializable]
    sealed class RuntimeCommandStartedEnvelope
    {
        public int protocol_version = RuntimeBridgeProtocol.Version;
        public string message_type = string.Empty;
        public string request_id = string.Empty;

        public RuntimeCommandStartedEnvelope() { }

        public RuntimeCommandStartedEnvelope(RuntimeBridgeMessage message)
        {
            message_type = message.message_type;
            request_id = message.request_id ?? string.Empty;
        }

        public RuntimeBridgeMessage ToMessage()
            => new()
            {
                protocol_version = protocol_version,
                message_type = message_type,
                request_id = request_id,
            };
    }

    [Serializable]
    sealed class RuntimeCommandResultEnvelope
    {
        public int protocol_version = RuntimeBridgeProtocol.Version;
        public string message_type = string.Empty;
        public string request_id = string.Empty;
        public RuntimeBridgeCommandResult? result;

        public RuntimeCommandResultEnvelope() { }

        public RuntimeCommandResultEnvelope(RuntimeBridgeMessage message)
        {
            message_type = message.message_type;
            request_id = message.request_id ?? string.Empty;
            result = message.result;
        }
    }
}
