using System.Text.Json.Serialization;

namespace Conduit;

public sealed class ToolExecutionResult
{
    public string Outcome { get; init; } = ToolOutcome.Success;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logs { get; init; }

    /// <summary>Earlier Unity logs, kept separate from this operation's output.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundLogs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnValue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolExceptionInfo? Exception { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Diagnostic { get; set; }

    public static ToolExecutionResult Success(string logs, string? returnValue = null, string? diagnostic = null, string? backgroundLogs = null) =>
        new()
        {
            Outcome = ToolOutcome.Success,
            Logs = ConduitText.NormalizeOptionalUserFacingText(logs),
            BackgroundLogs = ConduitText.NormalizeOptionalUserFacingText(backgroundLogs),
            ReturnValue = ConduitText.NormalizeOptionalPayloadText(returnValue),
            Diagnostic = ConduitText.NormalizeUserFacingText(diagnostic),
        };

    public static ToolExecutionResult NotConnected(string projectPath, string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.NotConnected,
            Diagnostic = ConduitText.NormalizeUserFacingText(
                diagnostic ?? $"Project '{projectPath}' is not connected to the MCP bridge."
            ),
        };

    public static ToolExecutionResult Timeout(TimeSpan timeout, string? diagnostic = null, string? backgroundLogs = null) =>
        new()
        {
            Outcome = ToolOutcome.Timeout,
            BackgroundLogs = ConduitText.NormalizeOptionalUserFacingText(backgroundLogs),
            Diagnostic = ConduitText.NormalizeUserFacingText(
                diagnostic ?? $"Unity did not report completion within {timeout}."
            ),
        };

    public static ToolExecutionResult Cancelled(string? diagnostic = null) =>
        new()
        {
            Outcome = ToolOutcome.Cancelled,
            Diagnostic = ConduitText.NormalizeUserFacingText(
                diagnostic ?? "The request was cancelled."
            ),
        };

    public static ToolExecutionResult DirtyScene(string diagnostic) =>
        new()
        {
            Outcome = ToolOutcome.DirtyScene,
            Diagnostic = ConduitText.NormalizeUserFacingText(diagnostic),
        };

    public static ToolExecutionResult FromException(
        Exception exception,
        string logs,
        string? diagnostic = null,
        string? backgroundLogs = null
    ) =>
        new()
        {
            Outcome = ToolOutcome.Exception,
            Logs = ConduitText.NormalizeOptionalUserFacingText(logs),
            BackgroundLogs = ConduitText.NormalizeOptionalUserFacingText(backgroundLogs),
            Exception = ToolExceptionInfo.FromException(exception),
            Diagnostic = ConduitText.NormalizeDiagnostic(diagnostic, exception.Message),
        };
}
