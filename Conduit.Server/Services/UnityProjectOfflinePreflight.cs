namespace Conduit;

static class UnityProjectOfflinePreflight
{
    public const string InvalidProjectDiagnostic
        = "The specified path is not a valid Unity project.";

    public const string OfflineDiagnostic
        = "No Unity editor process is running for this project. Use 'restart' to bring it online.";

    public const string MissingPackageDiagnostic
        = "A Unity editor process is running for this project, but the Conduit package does not appear to be installed.";

    public const string UnresponsiveBridgeDiagnostic
        = "A Unity editor process is running for this project, but the Unity connection is not responding.";

    public static async Task<OfflinePreflightResult> ExecuteAsync(
        string normalizedProjectPath,
        UnityProjectEnvironmentInspector environmentInspector,
        UnityProjectRegistry projectRegistry,
        UnityBridgeClient bridgeClient,
        TimeSpan noProcessTimeout,
        CancellationToken ct
    )
    {
        var snapshot = environmentInspector.Inspect(normalizedProjectPath);
        if (!snapshot.IsUnityProject)
        {
            projectRegistry.MarkReachable(normalizedProjectPath, false);
            return new(snapshot, null, true, InvalidProjectDiagnostic);
        }

        var timeout = snapshot.MatchedProcess is null ? noProcessTimeout : UnityToolTimeouts.StatusCommand;

        var probeExecution = await bridgeClient.ProbeAsync(
            normalizedProjectPath,
            snapshot.MatchedProcess?.ProcessId,
            timeout,
            ct
        );

        if (probeExecution.Handshake is not null || probeExecution.Result is not null)
            return new(snapshot, probeExecution, false, string.Empty);

        var blockedDiagnostic = ResolveBlockedDiagnostic(
            snapshot,
            probeExecution,
            snapshot.MatchedProcess is null ? null : environmentInspector.TryReadSafeModeDiagnostic(snapshot),
            environmentInspector.HasConduitPackageSignal(normalizedProjectPath)
        );

        if (string.IsNullOrWhiteSpace(blockedDiagnostic))
            return new(snapshot, null, false, string.Empty);

        projectRegistry.MarkReachable(normalizedProjectPath, false);
        return new(snapshot, null, true, blockedDiagnostic);
    }

    internal static string? ResolveBlockedDiagnostic(
        UnityProjectEnvironmentSnapshot snapshot,
        BridgeClientResult probeExecution,
        string? safeModeDiagnostic,
        bool hasConduitPackageSignal
    )
    {
        if (!snapshot.IsUnityProject)
            return InvalidProjectDiagnostic;

        if (!string.IsNullOrWhiteSpace(safeModeDiagnostic))
            return safeModeDiagnostic;

        if (snapshot.MatchedProcess is null)
            return OfflineDiagnostic;

        if (probeExecution.FailureKind is BridgeRuntimeFailureKind.InvalidHandshake
            or BridgeRuntimeFailureKind.ProjectMismatch
            or BridgeRuntimeFailureKind.HandshakeDisconnected
            or BridgeRuntimeFailureKind.ProcessExited)
            return probeExecution.FailureDiagnostic;

        return hasConduitPackageSignal
            ? UnresponsiveBridgeDiagnostic
            : MissingPackageDiagnostic;
    }
}

readonly struct OfflinePreflightResult(
    UnityProjectEnvironmentSnapshot snapshot,
    BridgeClientResult? probeExecution,
    bool isBlocked,
    string diagnostic)
{
    public UnityProjectEnvironmentSnapshot Snapshot { get; } = snapshot;

    public BridgeClientResult? ProbeExecution { get; } = probeExecution;

    public bool IsBlocked { get; } = isBlocked;

    public string Diagnostic { get; } = diagnostic;
}
