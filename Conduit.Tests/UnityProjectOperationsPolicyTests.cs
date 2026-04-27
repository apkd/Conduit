using JetBrains.Annotations;
using static Conduit.BridgeRuntimeFailureKind;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class UnityProjectOperationsPolicyTests
{
    static readonly BridgeProjectHandshake handshake = new()
    {
        ProjectPath = @"B:\Projects\Sample",
        DisplayName = "Sample",
        UnityVersion = "6000.0.0f1",
        SessionInstanceId = "session-1",
        LastSeenUtc = new(2026, 03, 23, 10, 0, 0, TimeSpan.Zero),
    };

    [Test]
    public async Task ReplayRequestAppliesToAnyRecoverableFailureAfterTheHandshake()
    {
        await Assert.That(UnityProjectOperations.ShouldReplayRequest(Failure(SendFailed, commandSent: false))).IsTrue();
        await Assert.That(UnityProjectOperations.ShouldReplayRequest(Failure(StartAckTimedOut, commandSent: true))).IsTrue();
        await Assert.That(UnityProjectOperations.ShouldReplayRequest(Failure(ResultDisconnected, commandSent: true))).IsTrue();
        await Assert.That(UnityProjectOperations.ShouldReplayRequest(Failure(ResultTimedOut, commandSent: true))).IsTrue();
        await Assert.That(UnityProjectOperations.ShouldReplayRequest(BridgeClientResult.Failure(null, SendFailed, "disconnected", false))).IsFalse();
    }

    [Test]
    public async Task ReachableStatusRequiresMoreThanAHandshake()
    {
        await Assert.That(UnityProjectOperations.ShouldReportReachableStatus(Failure(SendTimedOut, commandSent: false))).IsTrue();
        await Assert.That(UnityProjectOperations.ShouldReportReachableStatus(Failure(StartAckTimedOut, commandSent: true))).IsTrue();
        await Assert.That(UnityProjectOperations.ShouldReportReachableStatus(Failure(StartAckDisconnected, commandSent: true))).IsFalse();
        await Assert.That(UnityProjectOperations.ShouldReportReachableStatus(Failure(ResultTimedOut, commandSent: true))).IsTrue();
    }

    [Test]
    public async Task UnexpectedStatusFailurePreservesLiveButUnresponsiveDiagnosticWhenAProcessMatches()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Locked,
            runningUnityProcessCount: 1,
            matchedProcess: new(1234, @"C:\Program Files\Unity\Editor\Unity.exe", "Unity.exe -projectPath \"B:\\src\\SampleProject\"")
        );

        var result = UnityProjectOperations.BuildUnexpectedStatusFailureResult(
            snapshot.ProjectPath,
            snapshot,
            hasConduitPackageSignal: true,
            "Status probing failed unexpectedly."
        );

        await Assert.That(result.Outcome).IsEqualTo(ToolOutcome.NotConnected);
        await Assert.That(result.Diagnostic).IsEqualTo(
            $"{UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic} Status probing failed unexpectedly."
        );
    }

    [Test]
    public async Task UnexpectedStatusFailureDoesNotClaimAnUnresponsiveBridgeWithoutProcessEvidence()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Missing,
            runningUnityProcessCount: 0,
            matchedProcess: null
        );

        var result = UnityProjectOperations.BuildUnexpectedStatusFailureResult(
            snapshot.ProjectPath,
            snapshot,
            hasConduitPackageSignal: true,
            "Status probing failed unexpectedly."
        );

        await Assert.That(result.Outcome).IsEqualTo(ToolOutcome.NotConnected);
        await Assert.That(result.Diagnostic).IsEqualTo("Status probing failed unexpectedly.");
    }

    [Test]
    public async Task ProbeStatusRequiresAnActualCommandResult()
    {
        await Assert.That(UnityProjectOperations.ShouldUseProbeExecutionForStatus(BridgeClientResult.Connected(handshake))).IsFalse();
        await Assert.That(UnityProjectOperations.ShouldUseProbeExecutionForStatus(BridgeClientResult.Success(handshake, ToolExecutionResult.Success("{}")))).IsTrue();
    }

    [Test]
    public async Task ViewBurstAsmCommandPolicy_IsRegistered()
    {
        await Assert.That(BridgeCommandKinds.Parse(BridgeCommandTypes.ViewBurstAsm)).IsEqualTo(BridgeCommandKind.ViewBurstAsm);
        await Assert.That(UnityToolTimeouts.ForCommand(BridgeCommandKind.ViewBurstAsm)).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task RefreshRecoveryTreatsUpdatingAsBusy()
    {
        await Assert.That(RefreshAssetDatabaseRecoveryCoordinator.IsRefreshStillBusy(new() { IsUpdating = true })).IsTrue();
        await Assert.That(RefreshAssetDatabaseRecoveryCoordinator.IsRefreshStillBusy(new() { IsCompiling = true })).IsTrue();
        await Assert.That(RefreshAssetDatabaseRecoveryCoordinator.IsRefreshStillBusy(new() { ActiveCommandType = BridgeCommandTypes.RefreshAssetDatabase })).IsTrue();
        await Assert.That(RefreshAssetDatabaseRecoveryCoordinator.IsRefreshStillBusy(new())).IsFalse();
    }

    [Test]
    public async Task RefreshTimeoutDiagnosticIncludesLastObservedStateWithoutEditorLogActivity()
    {
        var diagnostic = RefreshAssetDatabaseRecoveryCoordinator.BuildTimeoutDiagnostic(
            Failure(ResultTimedOut, commandSent: true),
            BridgeCommandTypes.RefreshAssetDatabase,
            TimeSpan.FromMinutes(10),
            lastObservedState: "is_compiling=false, is_updating=true, active_command_type='refresh_asset_database'",
            lastStatusIssue: null
        );

        await Assert.That(diagnostic).Contains("Last observed status: is_compiling=false, is_updating=true, active_command_type='refresh_asset_database'.");
        await Assert.That(diagnostic).DoesNotContain("Editor.log activity");
    }

    static BridgeClientResult Failure(BridgeRuntimeFailureKind failureKind, bool commandSent) =>
        BridgeClientResult.Failure(handshake, failureKind, "diagnostic", commandSent);
}
