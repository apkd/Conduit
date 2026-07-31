namespace Conduit;

public sealed class BridgeClientResult
{
    public BridgeClientResult(
        BridgeProjectHandshake? handshake,
        ToolExecutionResult? result,
        BridgeRuntimeFailureKind? failureKind,
        string? failureDiagnostic,
        bool commandSent)
        : this(handshake, result, failureKind, failureDiagnostic, commandSent, null) { }

    internal BridgeClientResult(
        BridgeProjectHandshake? handshake,
        ToolExecutionResult? result,
        BridgeRuntimeFailureKind? failureKind,
        string? failureDiagnostic,
        bool commandSent,
        BridgeArtifact[]? artifacts)
    {
        Handshake = handshake;
        Result = result;
        FailureKind = failureKind;
        FailureDiagnostic = failureDiagnostic;
        CommandSent = commandSent;
        Artifacts = artifacts ?? [];
    }

    public BridgeProjectHandshake? Handshake { get; }

    public ToolExecutionResult? Result { get; }

    public BridgeRuntimeFailureKind? FailureKind { get; }

    public string? FailureDiagnostic { get; }

    public bool CommandSent { get; }

    internal BridgeArtifact[] Artifacts { get; }

    public static BridgeClientResult Connected(BridgeProjectHandshake handshake)
        => new(handshake, null, null, null, false);

    public static BridgeClientResult Success(
        BridgeProjectHandshake? handshake,
        ToolExecutionResult result,
        bool commandSent = true)
        => new(handshake, result, null, null, commandSent);

    internal static BridgeClientResult Success(
        BridgeProjectHandshake? handshake,
        ToolExecutionResult result,
        bool commandSent,
        BridgeArtifact[] artifacts)
        => new(handshake, result, null, null, commandSent, artifacts);

    public static BridgeClientResult Failure(
        BridgeProjectHandshake? handshake,
        BridgeRuntimeFailureKind failureKind,
        string diagnostic,
        bool commandSent
    ) => new(handshake, null, failureKind, diagnostic, commandSent);

    public BridgeClientResult WithHandshake(BridgeProjectHandshake handshake)
        => ReferenceEquals(Handshake, handshake)
            ? this
            : new(handshake, Result, FailureKind, FailureDiagnostic, CommandSent, Artifacts);
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
    AmbiguousTarget,
}
