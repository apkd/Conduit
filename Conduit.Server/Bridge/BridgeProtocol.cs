using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit;

public static class BridgeCommandTypes
{
    public const string Help = "help";
    public const string Restart = "restart";
    public const string Status = "status";
    public const string PlayMode = "playmode";
    public const string EditMode = "editmode";
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
    public const string ReimportAssets = "reimport_assets";
    public const string ExecuteCode = "execute_code";
    public const string ViewBurstAsm = "view_burst_asm";
    public const string Reflect = "reflect";
    public const string RunTestsEditMode = "run_tests_editmode";
    public const string RunTestsPlayMode = "run_tests_playmode";
    public const string RunTestsPlayer = "run_tests_player";
    public const string ProfilerRecord = "profiler_record";
    public const string ProfilerOverview = "profiler_overview";
    public const string ProfilerBrowse = "profiler_browse";
    internal const string ProfilerHasMarker = "profiler_has_marker";
    internal const string CompilationReferences = "compilation_references";
    internal const string AssemblyBlob = "assembly_blob";
}

static class BridgeMessageTypes
{
    public const string Hello = "hello";
    public const string Command = "command";
    public const string CancelCommand = "cancel_command";
    public const string CommandStarted = "command_started";
    public const string CommandResult = "command_result";
}

sealed class BridgeMessage
{
    public int ProtocolVersion { get; set; } = BridgeProtocol.Version;

    public string MessageType { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeProjectHandshake? Project { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeCommand? Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeCommandResult? Result { get; set; }

    public static BridgeMessage CreateHello(BridgeProjectHandshake project) =>
        new()
        {
            MessageType = BridgeMessageTypes.Hello,
            Project = project,
        };

    public static BridgeMessage CreateCommand(string requestId, BridgeCommand command) =>
        new()
        {
            MessageType = BridgeMessageTypes.Command,
            RequestId = requestId,
            Command = command,
        };

    public static BridgeMessage CreateCancelCommand(string requestId) =>
        new()
        {
            MessageType = BridgeMessageTypes.CancelCommand,
            RequestId = requestId,
        };

    public static BridgeMessage CreateCommandStarted(string requestId) =>
        new()
        {
            MessageType = BridgeMessageTypes.CommandStarted,
            RequestId = requestId,
        };

    public static BridgeMessage CreateCommandResult(string requestId, BridgeCommandResult result) =>
        new()
        {
            MessageType = BridgeMessageTypes.CommandResult,
            RequestId = requestId,
            Result = result,
        };
}

public sealed class BridgeProjectHandshake
{
    public string ProjectPath { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string UnityVersion { get; set; } = string.Empty;

    public int EditorProcessId { get; set; }

    public int ProcessId { get; set; }

    public string EndpointKind { get; set; } = BridgeEndpointKinds.Editor;

    public string Platform { get; set; } = string.Empty;

    public string BuildGuid { get; set; } = string.Empty;

    public string CloudProjectId { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public bool CanMonitorProcess { get; set; } = true;

    public string[] Capabilities { get; set; } = [];

    /// <summary>The effective editor log path for this Unity process.</summary>
    public string EditorLogPath { get; set; } = string.Empty;

    public string SessionInstanceId { get; set; } = string.Empty;

    public string HandoffToken { get; set; } = string.Empty;

    public DateTimeOffset LastSeenUtc { get; set; }

    [JsonIgnore]
    public int EffectiveProcessId => ProcessId > 0 ? ProcessId : EditorProcessId;

    [JsonIgnore]
    public bool IsPlayer => EndpointKind == BridgeEndpointKinds.Player;
}

sealed class BridgeCommand
{
    public string CommandType { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Snippet { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestFilter { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Async { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RebuildCache { get; set; }

    // internal bridge probes use the same command types and must not inflate MCP usage totals.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TrackUsage { get; set; }

    public string[] Args { get; set; } = [];

    public BridgeArtifact[] Artifacts { get; set; } = [];
}

sealed class BridgeCommandResult
{
    public string Outcome { get; set; } = ToolOutcome.Success;

    public string Logs { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeExceptionInfo? Exception { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Diagnostic { get; set; }

    public BridgeArtifact[] Artifacts { get; set; } = [];

    public ToolExecutionResult ToToolExecutionResult() =>
        new()
        {
            Outcome = Outcome,
            Logs = ConduitUtility.NormalizeOptionalUserFacingText(Logs),
            DisplayName = ConduitUtility.NormalizeOptionalUserFacingText(DisplayName),
            ReturnValue = ConduitUtility.NormalizeOptionalPayloadText(ReturnValue),
            Exception = TryNormalizeException(Exception),
            Diagnostic = ConduitUtility.NormalizeDiagnostic(Diagnostic, Exception?.Message),
        };

    static ToolExceptionInfo? TryNormalizeException(BridgeExceptionInfo? exception)
    {
        var type = ConduitUtility.NormalizeOptionalUserFacingText(exception?.Type);
        var message = ConduitUtility.NormalizeOptionalUserFacingText(exception?.Message);
        var stackTrace = ConduitUtility.NormalizeOptionalUserFacingText(exception?.StackTrace);
        return type == null && message == null && stackTrace == null
            ? null
            : ConduitUtility.ToToolExceptionInfo(type ?? string.Empty, message ?? string.Empty, stackTrace);
    }
}

sealed class BridgeArtifact
{
    const int EncodedChunkSize = 48 * 1024;

    public string Name { get; set; } = string.Empty;

    public string MediaType { get; set; } = "application/octet-stream";

    public string Sha256 { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RelativePath { get; set; }

    public string[] Chunks { get; set; } = [];

    public static BridgeArtifact FromBytes(string name, string mediaType, ReadOnlySpan<byte> bytes)
    {
        var encoded = Convert.ToBase64String(bytes);
        var chunkCount = Math.Max(1, (encoded.Length + EncodedChunkSize - 1) / EncodedChunkSize);
        var chunks = new string[chunkCount];
        for (var index = 0; index < chunkCount; index++)
        {
            var start = index * EncodedChunkSize;
            chunks[index] = encoded.Substring(start, Math.Min(EncodedChunkSize, encoded.Length - start));
        }

        return new()
        {
            Name = name,
            MediaType = mediaType,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            Chunks = chunks,
        };
    }

    public byte[] Decode()
    {
        var encoded = string.Concat(Chunks);
        var bytes = Convert.FromBase64String(encoded);
        var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!string.Equals(actualHash, Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact '{Name}' failed SHA-256 verification.");

        return bytes;
    }
}

sealed class BridgeExceptionInfo
{
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; set; }
}

static class BridgeProtocol
{
    public const int Version = 4;

    public static string Serialize(BridgeMessage message) =>
        JsonSerializer.Serialize(message, ConduitJsonContext.Default.BridgeMessage);

    public static BridgeMessage? Deserialize(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize(payload, ConduitJsonContext.Default.BridgeMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
