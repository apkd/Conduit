namespace Conduit;

readonly record struct OfflinePreflightResult(
    UnityProjectEnvironmentSnapshot Snapshot,
    BridgeClientResult? ProbeExecution,
    bool IsBlocked,
    string Diagnostic
);
