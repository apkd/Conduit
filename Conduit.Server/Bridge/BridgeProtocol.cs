using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conduit;

public static class BridgeCommandTypes
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
    public const string ViewBurstAsm = "view_burst_asm";
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

sealed class BridgeMessage
{
    public int ProtocolVersion { get; set; } = 2;

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

    public string SessionInstanceId { get; set; } = string.Empty;

    public DateTimeOffset LastSeenUtc { get; set; }
}

sealed class BridgeCommand
{
    public string CommandType { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Target { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Snippet { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestFilter { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RebuildCache { get; set; }

    public string[] Args { get; set; } = [];
}

sealed class BridgeCommandResult
{
    public string Outcome { get; set; } = ToolOutcome.Success;

    public string Logs { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnValue { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BridgeExceptionInfo? Exception { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Diagnostic { get; set; }

    public ToolExecutionResult ToToolExecutionResult() =>
        new()
        {
            Outcome = Outcome,
            Logs = ConduitUtility.NormalizeOptionalUserFacingText(Logs),
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

sealed class BridgeExceptionInfo
{
    public string Type { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; set; }
}

static class BridgeProtocol
{
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
