using System.Text.Json.Serialization;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Conduit;

public static class ToolOutcome
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

public static class ProjectStatus
{
    public const string ConnectedIdle = "connected_idle";
    public const string ConnectedBusy = "connected_busy";
    public const string Offline = "offline";
}

public sealed class ProjectListItem
{
    public string ProjectPath { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string UnityVersion { get; init; } = string.Empty;

    public string LastSeenUtc { get; init; } = string.Empty;

    public string Status { get; init; } = ProjectStatus.Offline;
}

public sealed class ToolExecutionResult
{
    public string Outcome { get; init; } = ToolOutcome.Success;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnValue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolExceptionInfo? Exception { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Diagnostic { get; set; }

    public static ToolExecutionResult Success(string logs, string? returnValue = null, string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.Success,
            Logs = ConduitUtility.NormalizeOptionalUserFacingText(logs),
            ReturnValue = ConduitUtility.NormalizeOptionalPayloadText(returnValue),
            Diagnostic = ConduitUtility.NormalizeUserFacingText(diagnostic),
        };

    public static ToolExecutionResult NotConnected(string projectPath, string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.NotConnected,
            Diagnostic = ConduitUtility.NormalizeUserFacingText(diagnostic ?? $"Project '{projectPath}' is not connected to the MCP bridge."),
        };

    public static ToolExecutionResult Timeout(TimeSpan timeout, string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.Timeout,
            Diagnostic = ConduitUtility.NormalizeUserFacingText(diagnostic ?? $"Unity did not report completion within {timeout}."),
        };

    public static ToolExecutionResult Cancelled(string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.Cancelled,
            Diagnostic = ConduitUtility.NormalizeUserFacingText(diagnostic ?? "The request was cancelled."),
        };

    public static ToolExecutionResult DirtyScene(string diagnostic) =>
        new()
        {
            Outcome = ToolOutcome.DirtyScene,
            Diagnostic = ConduitUtility.NormalizeUserFacingText(diagnostic),
        };

    public static ToolExecutionResult FromException(Exception exception, string logs, string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.Exception,
            Logs = ConduitUtility.NormalizeOptionalUserFacingText(logs),
            Exception = ToolExceptionInfo.FromException(exception),
            Diagnostic = ConduitUtility.NormalizeDiagnostic(diagnostic, exception.Message),
        };
}

public sealed class ToolExceptionInfo
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; init; }

    public static ToolExceptionInfo FromException(Exception exception) =>
        ConduitUtility.ToToolExceptionInfo(exception);
}
