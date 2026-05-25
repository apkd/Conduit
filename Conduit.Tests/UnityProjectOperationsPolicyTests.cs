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
    public async Task UnexpectedStatusFailureReportsMissingPackageWhenNoPackageSignalExists()
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
            hasConduitPackageSignal: false,
            "Status probing failed unexpectedly."
        );

        await Assert.That(result.Outcome).IsEqualTo(ToolOutcome.NotConnected);
        await Assert.That(result.Diagnostic).IsEqualTo(
            $"{UnityProjectOfflinePreflight.MissingPackageDiagnostic} Status probing failed unexpectedly."
        );
    }

    [Test]
    public async Task CommandFailureReportsMissingPackageWhenConnectionTimesOutWithoutPackageSignal()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Missing,
            runningUnityProcessCount: 0,
            matchedProcess: null
        );
        var connectionFailure = BridgeClientResult.Failure(
            handshake: null,
            ConnectTimedOut,
            "Could not establish a Unity connection.",
            commandSent: false
        );

        var result = UnityProjectOperations.ToToolExecutionResult(
            snapshot.ProjectPath,
            BridgeCommandTypes.RefreshAssetDatabase,
            connectionFailure,
            TimeSpan.FromSeconds(5),
            new UnityProjectEnvironmentInspector(),
            snapshot
        );

        await Assert.That(result.Outcome).IsEqualTo(ToolOutcome.NotConnected);
        await Assert.That(result.Diagnostic).IsEqualTo(UnityProjectOfflinePreflight.MissingPackageDiagnostic);
    }

    [Test]
    public async Task ProbeStatusRequiresAnActualCommandResult()
    {
        await Assert.That(UnityProjectOperations.ShouldUseProbeExecutionForStatus(BridgeClientResult.Connected(handshake))).IsFalse();
        await Assert.That(UnityProjectOperations.ShouldUseProbeExecutionForStatus(BridgeClientResult.Success(handshake, ToolExecutionResult.Success("{}")))).IsTrue();
    }

    [Test]
    public async Task StatusDiagnosticsUseNeutralUnityProcessLabel()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Locked,
            runningUnityProcessCount: 1,
            matchedProcess: new(1234, "/home/apk/Unity/Hub/Editor/6000.4.0f1/Editor/Unity", "Unity -projectPath \"/mnt/b/src/SampleProject\"")
        );

        var text = UnityProjectStatusFormatter.FormatPingFailure(
            snapshot,
            ToolExecutionResult.NotConnected(snapshot.ProjectPath, "offline"),
            processRuntime: null,
            CompilationDiagnosticSummary.Empty,
            editorLogPath: null
        );

        await Assert.That(text).Contains("Unity editor processes running: 1");
        await Assert.That(text).DoesNotContain("Unity.exe processes");
    }

    [Test]
    public async Task BridgeCommandPolicies_AreRegistered()
    {
        await Assert.That(BridgeCommandKinds.Parse(BridgeCommandTypes.ViewBurstAsm)).IsEqualTo(BridgeCommandKind.ViewBurstAsm);
        await Assert.That(UnityToolTimeouts.ForCommand(BridgeCommandKind.ViewBurstAsm)).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(BridgeCommandKinds.Parse(BridgeCommandTypes.Reflect)).IsEqualTo(BridgeCommandKind.Reflect);
        await Assert.That(UnityToolTimeouts.ForCommand(BridgeCommandKind.Reflect)).IsEqualTo(TimeSpan.FromSeconds(90));
        await Assert.That(BridgeCommandKinds.Parse(BridgeCommandTypes.ProfilerRecord)).IsEqualTo(BridgeCommandKind.ProfilerRecord);
        await Assert.That(UnityToolTimeouts.ForCommand(BridgeCommandKind.ProfilerRecord)).IsEqualTo(TimeSpan.FromMinutes(2));
        await Assert.That(BridgeCommandKinds.Parse(BridgeCommandTypes.ProfilerOverview)).IsEqualTo(BridgeCommandKind.ProfilerOverview);
        await Assert.That(UnityToolTimeouts.ForCommand(BridgeCommandKind.ProfilerOverview)).IsEqualTo(TimeSpan.FromSeconds(90));
        await Assert.That(BridgeCommandKinds.Parse(BridgeCommandTypes.ProfilerBrowse)).IsEqualTo(BridgeCommandKind.ProfilerBrowse);
        await Assert.That(UnityToolTimeouts.ForCommand(BridgeCommandKind.ProfilerBrowse)).IsEqualTo(TimeSpan.FromSeconds(90));
    }

    [Test]
    public async Task ReflectValidation_RejectsMissingModeBeforeQueueingUnityWork()
    {
        var result = UnityProjectOperations.ValidateReflectRequest(" ");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Outcome).IsEqualTo(ToolOutcome.Exception);
        await Assert.That(result.Diagnostic).IsEqualTo(UnityProjectOperations.ReflectMissingModeDiagnostic);
        await Assert.That(result.Diagnostic).Contains("interfaces");
        await Assert.That(result.Diagnostic).Contains("delegates");
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

    [Test]
    public async Task ExecuteCodePolicyRejectsAssetDatabaseRefresh()
    {
        await Assert.That(UnityProjectOperations.CallsAssetDatabaseRefresh("UnityEditor.AssetDatabase.Refresh();")).IsTrue();
    }

    [Test]
    public async Task ExecuteCodePolicyRejectsImportedAssetDatabaseRefresh()
    {
        await Assert.That(UnityProjectOperations.CallsAssetDatabaseRefresh("AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);")).IsTrue();
    }

    [Test]
    public async Task ExecuteCodePolicyRejectsAssetDatabaseRefreshWithWhitespace()
    {
        await Assert.That(UnityProjectOperations.CallsAssetDatabaseRefresh("UnityEditor . AssetDatabase . Refresh ();")).IsTrue();
    }

    [Test]
    public async Task ExecuteCodePolicyAllowsUnrelatedSnippets()
    {
        await Assert.That(UnityProjectOperations.CallsAssetDatabaseRefresh("Debug.Log(42);")).IsFalse();
        await Assert.That(UnityProjectOperations.AssetDatabaseRefreshDiagnostic).Contains("`refresh_asset_database` tool instead");
    }

    static BridgeClientResult Failure(BridgeRuntimeFailureKind failureKind, bool commandSent) =>
        BridgeClientResult.Failure(handshake, failureKind, "diagnostic", commandSent);
}
