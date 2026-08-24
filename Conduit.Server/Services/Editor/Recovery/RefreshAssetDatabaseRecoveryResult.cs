namespace Conduit;

readonly record struct RefreshAssetDatabaseRecoveryResult(
    ToolExecutionResult Result,
    int? MonitoredProcessId,
    bool Reachable
);
