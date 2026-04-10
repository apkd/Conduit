namespace Conduit;

public sealed class BridgeClientResult(
    BridgeProjectHandshake? handshake,
    ToolExecutionResult? result,
    BridgeRuntimeFailureKind? failureKind,
    string? failureDiagnostic,
    bool commandSent)
{
    public BridgeProjectHandshake? Handshake { get; } = handshake;

    public ToolExecutionResult? Result { get; } = result;

    public BridgeRuntimeFailureKind? FailureKind { get; } = failureKind;

    public string? FailureDiagnostic { get; } = failureDiagnostic;

    public bool CommandSent { get; } = commandSent;

    public static BridgeClientResult Connected(BridgeProjectHandshake handshake)
        => new(handshake, null, null, null, false);

    public static BridgeClientResult Success(BridgeProjectHandshake? handshake, ToolExecutionResult result, bool commandSent = true)
        => new(handshake, result, null, null, commandSent);

    public static BridgeClientResult Failure(
        BridgeProjectHandshake? handshake,
        BridgeRuntimeFailureKind failureKind,
        string diagnostic,
        bool commandSent
    ) => new(handshake, null, failureKind, diagnostic, commandSent);

    public BridgeClientResult WithHandshake(BridgeProjectHandshake handshake)
        => ReferenceEquals(Handshake, handshake)
            ? this
            : new(handshake, Result, FailureKind, FailureDiagnostic, CommandSent);
}

public enum BridgeRuntimeFailureKind
{
    ConnectTimedOut,
    HandshakeDisconnected,
    InvalidHandshake,
    ProjectMismatch,
    SendFailed,
    SendTimedOut,
    StartAckDisconnected,
    StartAckTimedOut,
    ResultDisconnected,
    ResultTimedOut,
    ProcessExited,
}
