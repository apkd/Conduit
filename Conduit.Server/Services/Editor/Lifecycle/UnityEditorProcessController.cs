using System.Diagnostics;
using System.Text;

namespace Conduit;

public sealed partial class UnityEditorProcessController(
    UnityBridgeClient bridgeClient,
    UnityProjectEnvironmentInspector environmentInspector,
    UnityProjectRegistry projectRegistry)
{
    internal const string RestartedProcessExitedDiagnostic = "The restarted Unity process has shut down or crashed.";

    internal async Task<ToolExecutionResult> RestartAsync(
        string projectPath,
        bool trackUsage,
        IReadOnlyList<string>? editorArguments,
        IReadOnlyDictionary<string, string?>? environmentVariables,
        CancellationToken ct
    )
    {
        ValidateEditorArguments(editorArguments);
        UnityEditorLaunchEnvironment.ValidateRestartEnvironmentOverrides(environmentVariables);

        // the replacement editor can recover this per-call state only from its launch environment.
        long? restartStartedUtcTicks = trackUsage ? DateTime.UtcNow.Ticks : null;
        var builder = new StringBuilder();
        string? restartLogPath = null;
        Process? editorProcess = null;
        Process? restartedProcess = null;
        IReadOnlyDictionary<string, string>? editorEnvironment = null;

        try
        {
            var snapshot = environmentInspector.Inspect(projectPath);
            if (!snapshot.IsUnityProject)
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, "The specified path is not a valid Unity project.");

            if (!environmentInspector.HasConduitPackageSignal(snapshot.ProjectPath))
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, UnityProjectOfflinePreflight.MissingPackageDiagnostic);

            var dirtySceneResult = await TryCreateDirtySceneBlockAsync(snapshot.ProjectPath, snapshot.MatchedProcess?.ProcessId, ct);
            if (dirtySceneResult is not null)
                return dirtySceneResult;

            builder.AppendLine($"Project: {snapshot.ProjectPath}");

            using var restartLock = await ProjectRestartLock.AcquireAsync(snapshot.ProjectPath, ct);
            if (restartLock.WasContended)
            {
                builder.AppendLine("Waited for another Conduit process to finish restarting this project.");
                var completedRestartProbe = await bridgeClient.ProbeAsync(snapshot.ProjectPath, null, ct);
                if (completedRestartProbe.Handshake != null)
                {
                    builder.AppendLine("The Unity connection from that restart is responsive.");
                    return ToolExecutionResult.Success(string.Empty, builder.ToTrimmedString());
                }
            }

            snapshot = environmentInspector.Inspect(snapshot.ProjectPath);

            var probe = await bridgeClient.ProbeAsync(snapshot.ProjectPath, snapshot.MatchedProcess?.ProcessId, ct);
            var handshake = probe.Handshake;
            if (handshake != null)
            {
                editorProcess = ProcessInspection.TryGetProcess(handshake.EditorProcessId);
                if (editorProcess != null)
                {
                    builder.AppendLine($"Found running Unity editor via bridge: pid={editorProcess.Id}");
                    editorEnvironment = UnityEditorLaunchEnvironment.TryReadProcessEnvironment(editorProcess.Id);
                    if (await TryTerminateExistingEditorAsync(editorProcess, builder, ct) is { } terminationResult)
                        return terminationResult;
                }
            }
            else if (snapshot.MatchedProcess is { } matchedProcess)
            {
                editorProcess = ProcessInspection.TryGetProcess(matchedProcess.ProcessId);
                if (editorProcess != null)
                {
                    builder.AppendLine($"Found running Unity editor via command line: pid={editorProcess.Id}");
                    editorEnvironment = UnityEditorLaunchEnvironment.TryReadProcessEnvironment(editorProcess.Id);
                    if (await TryTerminateExistingEditorAsync(editorProcess, builder, ct) is { } terminationResult)
                        return terminationResult;
                }
            }
            else if (snapshot.LockfileState == UnityProjectLockfileState.Locked)
            {
                builder.AppendLine("Bridge is unreachable while the project lockfile is still held.");
                builder.AppendLine("No exact Unity.exe process could be matched to this project.");
                builder.AppendLine("Conduit did not launch another editor because Unity would display a project-lock modal.");
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, builder.ToTrimmedString());
            }

            var editorPath = environmentInspector.ResolveUnityEditorPath(snapshot, editorProcess);
            if (string.IsNullOrWhiteSpace(editorPath))
            {
                builder.AppendLine("Could not locate a Unity editor executable for this project.");
                return ToolExecutionResult.Success(string.Empty, builder.ToTrimmedString());
            }

            var preservedBackupPaths = PreserveSceneBackups(snapshot.ProjectPath);
            if (preservedBackupPaths.Length > 0)
                builder.AppendLine($"Preserved {preservedBackupPaths.Length} scene backup(s) in Assets/_Recovery.");

            restartLogPath = environmentInspector.GetRestartLogPath(snapshot.ProjectPath);
            await projectRegistry.RememberEditorLogPathAsync(
                snapshot.ProjectPath,
                restartLogPath,
                processId: null,
                ct
            );
            if (Path.GetDirectoryName(restartLogPath) is { Length: > 0 } logDirectoryPath)
                Directory.CreateDirectory(logDirectoryPath);
            PrepareRestartLogPath(restartLogPath);

            var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(snapshot.ProjectPath);
            var startInfo = CreateLaunchStartInfo(
                editorPath,
                platformProjectPath,
                restartLogPath,
                editorArguments
            );
            UnityEditorLaunchEnvironment.ApplyRestartProcessEnvironment(startInfo, editorEnvironment);
            // apply caller state last so null entries can remove inherited editor and service variables.
            UnityEditorLaunchEnvironment.ApplyRestartEnvironmentOverrides(startInfo, environmentVariables);
            UnityEditorLaunchEnvironment.ApplyRestartUsageTracking(startInfo, restartStartedUtcTicks);

            var prelaunchSnapshot = environmentInspector.Inspect(snapshot.ProjectPath);
            if (prelaunchSnapshot.MatchedProcess != null
                || prelaunchSnapshot.LockfileState == UnityProjectLockfileState.Locked)
            {
                builder.AppendLine("Another Unity editor claimed the project before launch.");
                builder.AppendLine("Conduit did not launch a duplicate editor.");
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, builder.ToTrimmedString());
            }

            SystemdProcessIsolation.TryApply(startInfo);
            restartedProcess = Process.Start(startInfo);

            if (restartedProcess == null)
            {
                builder.AppendLine("Failed to start the Unity editor process.");
                return ToolExecutionResult.NotConnected(snapshot.ProjectPath, builder.ToTrimmedString());
            }

            builder.AppendLine($"Started Unity editor: {editorPath}");
            return await WaitForRestartedEditorAsync(snapshot.ProjectPath, restartLogPath, restartedProcess, builder, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            builder.AppendLine("Restart canceled.");
            return ToolExecutionResult.Success(string.Empty, builder.ToTrimmedString());
        }
        catch (Exception exception)
        {
            builder.AppendLine($"Restart encountered an exception: {exception.Message}");
            return ToolExecutionResult.Success(string.Empty, builder.ToTrimmedString());
        }
        finally
        {
            restartedProcess?.Dispose();
            editorProcess?.Dispose();
        }
    }

    async Task<ToolExecutionResult> WaitForRestartedEditorAsync(
        string projectPath,
        string restartLogPath,
        Process restartedProcess,
        StringBuilder builder,
        CancellationToken ct
    )
    {
        var startupStartedUtc = DateTimeOffset.UtcNow;
        var startupDeadlineUtc = startupStartedUtc + UnityToolTimeouts.RestartStartupMax;
        var currentWindowDeadlineUtc = startupStartedUtc + UnityToolTimeouts.RestartStartupWindow;
        var previousLogSnapshot = environmentInspector.GetEditorLogSnapshot(restartLogPath);

        while (true)
        {
            var pingExecution = await bridgeClient.ExecuteCommandAsync(
                projectPath,
                BridgeIdentifiers.CreateRequestId(),
                new() { CommandType = BridgeCommandTypes.Status },
                UnityToolTimeouts.StatusCommand,
                processIdHint: null,
                ct
            );

            var restartCompilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(restartLogPath);
            if (pingExecution.Result?.Outcome == ToolOutcome.Success)
            {
                builder.AppendLine("Unity connection became responsive.");
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.Success(string.Empty, builder.ToTrimmedString());
            }

            if (HasExited(restartedProcess))
                return ToolExecutionResult.NotConnected(projectPath, RestartedProcessExitedDiagnostic);

            var currentSnapshot = environmentInspector.Inspect(projectPath);
            if (environmentInspector.TryReadSafeModeDiagnostic(currentSnapshot) is { } safeModeDiagnostic)
            {
                builder.AppendLine(safeModeDiagnostic);
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.NotConnected(projectPath, builder.ToTrimmedString());
            }

            if (!string.IsNullOrWhiteSpace(restartCompilationDiagnostics.ErrorText))
            {
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.Success(string.Empty, builder.ToTrimmedString());
            }

            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc >= startupDeadlineUtc)
            {
                builder.AppendLine(
                    $"Startup timed out after {UnityToolTimeouts.RestartStartupMax.TotalMinutes:0} minutes despite continued restart log activity."
                );
                AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                return ToolExecutionResult.Timeout(UnityToolTimeouts.RestartStartupMax, builder.ToTrimmedString());
            }

            if (nowUtc >= currentWindowDeadlineUtc)
            {
                var currentLogSnapshot = environmentInspector.GetEditorLogSnapshot(restartLogPath);
                if (!TryExtendRestartStartupWindow(
                        currentWindowDeadlineUtc,
                        startupDeadlineUtc,
                        previousLogSnapshot,
                        currentLogSnapshot,
                        out var nextWindowDeadlineUtc
                    ))
                {
                    builder.AppendLine(
                        $"Startup timed out after {(nowUtc - startupStartedUtc).TotalSeconds:0} seconds because "
                        + "the restart log stopped changing while waiting for a responsive bridge or a compilation failure."
                    );
                    AppendLatestCompilationDiagnostics(builder, restartCompilationDiagnostics);
                    return ToolExecutionResult.Timeout(nowUtc - startupStartedUtc, builder.ToTrimmedString());
                }

                previousLogSnapshot = currentLogSnapshot;
                currentWindowDeadlineUtc = nextWindowDeadlineUtc;
                continue;
            }

            var delay = currentWindowDeadlineUtc - nowUtc;
            if (delay > UnityToolTimeouts.RestartStartupPollInterval)
                delay = UnityToolTimeouts.RestartStartupPollInterval;
            if (delay <= TimeSpan.Zero)
                continue;

            await Task.Delay(delay, ct);
        }
    }
}
