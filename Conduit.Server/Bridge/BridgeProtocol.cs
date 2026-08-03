using System.Text.Json;

namespace Conduit;

static class BridgeCommandResultExtensions
{
    public static ToolExecutionResult ToToolExecutionResult(this BridgeCommandResult result) =>
        new()
        {
            Outcome = result.Outcome,
            Logs = ConduitUtility.NormalizeOptionalUserFacingText(result.Logs),
            DisplayName = ConduitUtility.NormalizeOptionalUserFacingText(result.DisplayName),
            ReturnValue = ConduitUtility.NormalizeOptionalPayloadText(result.ReturnValue),
            Exception = TryNormalizeException(result.Exception),
            Diagnostic = ConduitUtility.NormalizeDiagnostic(result.Diagnostic, result.Exception?.Message),
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

static class ServerBridgeArtifactExtensions
{
    public static byte[] Decode(this BridgeArtifact artifact) => artifact.ReadVerified();
}

static class BridgeProtocol
{
    public const int Version = BridgeContract.Version;

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
