namespace Conduit;

static class BridgeCommandResultExtensions
{
    internal static ToolExecutionResult ToToolExecutionResult(this BridgeCommandResult result) =>
        new()
        {
            Outcome = result.Outcome,
            Logs = ConduitText.NormalizeOptionalUserFacingText(result.Logs),
            DisplayName = ConduitText.NormalizeOptionalUserFacingText(result.DisplayName),
            ReturnValue = ConduitText.NormalizeOptionalPayloadText(result.ReturnValue),
            Exception = TryNormalizeException(result.Exception),
            Diagnostic = ConduitText.NormalizeDiagnostic(result.Diagnostic, result.Exception?.Message),
        };

    static ToolExceptionInfo? TryNormalizeException(BridgeExceptionInfo? exception)
    {
        var type = ConduitText.NormalizeOptionalUserFacingText(exception?.Type);
        var message = ConduitText.NormalizeOptionalUserFacingText(exception?.Message);
        var stackTrace = ConduitText.NormalizeOptionalUserFacingText(exception?.StackTrace);
        return type == null && message == null && stackTrace == null
            ? null
            : ConduitText.ToToolExceptionInfo(
                type ?? string.Empty,
                message ?? string.Empty,
                stackTrace
            );
    }
}
