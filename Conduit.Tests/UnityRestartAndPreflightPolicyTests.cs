using System.Diagnostics;
using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class UnityRestartAndPreflightPolicyTests
{
    [Test]
    public async Task RestartLaunchArgumentsIncludeAbsoluteProjectLogPath()
    {
        var projectPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-project-{Guid.NewGuid():N}"));
        var logPath = Path.GetFullPath(Path.Combine(projectPath, "Logs", "Editor.log"));

        var arguments = UnityEditorProcessController.BuildLaunchArguments(projectPath, logPath);

        await Assert.That(arguments).Contains($"-projectPath \"{projectPath}\"");
        await Assert.That(arguments).Contains($"-logFile \"{logPath}\"");
    }

    [Test]
    public async Task PrepareRestartLogPathClearsExistingLogContent()
    {
        var projectPath = CreateTempProject();
        try
        {
            var logDirectoryPath = Path.Combine(projectPath, "Logs");
            Directory.CreateDirectory(logDirectoryPath);
            var logPath = Path.Combine(logDirectoryPath, "Editor.log");
            await File.WriteAllTextAsync(
                logPath,
                """
                ## Script Compilation Error
                stale error
                """
            );

            UnityEditorProcessController.PrepareRestartLogPath(logPath);

            await Assert.That(File.Exists(logPath)).IsTrue();
            await Assert.That(new FileInfo(logPath).Length).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task SafeModeBlockedPreflightPreservesSafeModeDiagnostic()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Locked,
            runningUnityProcessCount: 1,
            matchedProcess: new(1234, @"C:\Program Files\Unity\Editor\Unity.exe", "Unity.exe -projectPath \"B:\\src\\SampleProject\"")
        );

        const string safeModeDiagnostic = "Unity editor appears to be in Safe Mode.";
        var connectTimeout = BridgeClientResult.Failure(
            handshake: null,
            BridgeRuntimeFailureKind.ConnectTimedOut,
            "timeout",
            commandSent: false
        );

        var blockedDiagnostic = UnityProjectOfflinePreflight.ResolveBlockedDiagnostic(
            snapshot,
            connectTimeout,
            safeModeDiagnostic,
            hasConduitPackageSignal: true
        );

        await Assert.That(blockedDiagnostic).IsEqualTo(safeModeDiagnostic);
    }

    [Test]
    [Arguments(false, false, UnityProjectOfflinePreflight.InvalidProjectDiagnostic)]
    [Arguments(true, false, UnityProjectOfflinePreflight.OfflineDiagnostic)]
    [Arguments(true, true, UnityProjectOfflinePreflight.MissingPackageDiagnostic)]
    [Arguments(true, true, UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic)]
    public async Task BlockedPreflightDistinguishesProjectProcessAndPackageState(
        bool isUnityProject,
        bool hasMatchedProcess,
        string expectedDiagnostic
    )
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: isUnityProject,
            editorVersion: "6000.4.0f1",
            lockfileState: hasMatchedProcess ? UnityProjectLockfileState.Locked : UnityProjectLockfileState.Missing,
            runningUnityProcessCount: hasMatchedProcess ? 1 : 0,
            matchedProcess: hasMatchedProcess
                ? new(1234, @"C:\Program Files\Unity\Editor\Unity.exe", "Unity.exe -projectPath \"B:\\src\\SampleProject\"")
                : null
        );

        var connectTimeout = BridgeClientResult.Failure(
            handshake: null,
            BridgeRuntimeFailureKind.ConnectTimedOut,
            "timeout",
            commandSent: false
        );

        var hasConduitPackageSignal = expectedDiagnostic == UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic;

        var blockedDiagnostic = UnityProjectOfflinePreflight.ResolveBlockedDiagnostic(
            snapshot,
            connectTimeout,
            safeModeDiagnostic: null,
            hasConduitPackageSignal
        );

        await Assert.That(blockedDiagnostic).IsEqualTo(expectedDiagnostic);
    }

    [Test]
    public async Task MatchedProcessWithSpecificBridgeFailurePreservesFailureDiagnostic()
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/mnt/b/src/SampleProject",
            isUnityProject: true,
            editorVersion: "6000.4.0f1",
            lockfileState: UnityProjectLockfileState.Locked,
            runningUnityProcessCount: 1,
            matchedProcess: new(1234, @"C:\Program Files\Unity\Editor\Unity.exe", "Unity.exe -projectPath \"B:\\src\\SampleProject\"")
        );

        var invalidHandshake = BridgeClientResult.Failure(
            handshake: null,
            BridgeRuntimeFailureKind.InvalidHandshake,
            "Unity returned an invalid hello handshake for '/mnt/b/src/SampleProject'.",
            commandSent: false
        );

        var blockedDiagnostic = UnityProjectOfflinePreflight.ResolveBlockedDiagnostic(
            snapshot,
            invalidHandshake,
            safeModeDiagnostic: null,
            hasConduitPackageSignal: true
        );

        await Assert.That(blockedDiagnostic).IsEqualTo("Unity returned an invalid hello handshake for '/mnt/b/src/SampleProject'.");
    }

    [Test]
    public async Task HasExitedReturnsTrueForExitedProcess()
    {
        using var process = StartShortLivedProcess();
        process.WaitForExit();

        await Assert.That(UnityEditorProcessController.HasExited(process)).IsTrue();
    }

    [Test]
    public async Task PreserveSceneBackupsCopiesRenamesAndDeletesOriginalFiles()
    {
        var projectPath = CreateTempProject();
        try
        {
            var backupDirectoryPath = Path.Combine(projectPath, "Temp", "__BackupScenes");
            Directory.CreateDirectory(backupDirectoryPath);
            var backupFilePath = Path.Combine(backupDirectoryPath, "SampleScene.backup");
            File.WriteAllText(backupFilePath, "scene-backup");

            var copiedPaths = UnityEditorProcessController.PreserveSceneBackups(projectPath);

            var recoveryFilePath = Path.Combine(projectPath, "Assets", "_Recovery", "SampleScene.unity");
            var copiedPath = await Assert.That(copiedPaths).HasSingleItem();
            await Assert.That(copiedPath).IsEqualTo(recoveryFilePath);
            await Assert.That(File.Exists(recoveryFilePath)).IsTrue();
            await Assert.That(File.ReadAllText(recoveryFilePath)).IsEqualTo("scene-backup");
            await Assert.That(File.Exists(backupFilePath)).IsFalse();
            await Assert.That(Directory.Exists(backupDirectoryPath)).IsFalse();
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task PreserveSceneBackupsAvoidsRecoveryNameCollisions()
    {
        var projectPath = CreateTempProject();
        try
        {
            var backupDirectoryPath = Path.Combine(projectPath, "Temp", "__BackupScenes");
            var recoveryDirectoryPath = Path.Combine(projectPath, "Assets", "_Recovery");
            Directory.CreateDirectory(backupDirectoryPath);
            Directory.CreateDirectory(recoveryDirectoryPath);
            File.WriteAllText(Path.Combine(recoveryDirectoryPath, "SampleScene.unity"), "existing-scene");
            File.WriteAllText(Path.Combine(backupDirectoryPath, "SampleScene.backup"), "first-backup");
            File.WriteAllText(Path.Combine(backupDirectoryPath, "SampleScene.unity.backup"), "second-backup");

            var copiedPaths = UnityEditorProcessController.PreserveSceneBackups(projectPath);

            await Assert.That(copiedPaths.Length).IsEqualTo(2);
            foreach (var path in copiedPaths)
            {
                await Assert.That(path).StartsWith(recoveryDirectoryPath).WithComparison(StringComparison.OrdinalIgnoreCase);
                await Assert.That(path).EndsWith(".unity").WithComparison(StringComparison.OrdinalIgnoreCase);
                await Assert.That(path).IsNotEqualTo(Path.Combine(recoveryDirectoryPath, "SampleScene.unity"));
                await Assert.That(File.Exists(path)).IsTrue();
            }

            await Assert.That(File.ReadAllText(Path.Combine(recoveryDirectoryPath, "SampleScene.unity"))).IsEqualTo("existing-scene");
            await Assert.That(
                    copiedPaths
                        .Select(File.ReadAllText)
                        .OrderBy(static value => value, StringComparer.Ordinal)
                        .ToArray()
                )
                .IsEquivalentTo(new[] { "first-backup", "second-backup" });
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    [Test]
    public async Task RestartStartupWindowExtendsByOneMinuteWhenTheLogKeepsChanging()
    {
        var startupDeadlineUtc = DateTimeOffset.UtcNow + UnityToolTimeouts.RestartStartupMax;
        var currentWindowDeadlineUtc = DateTimeOffset.UtcNow + UnityToolTimeouts.RestartStartupWindow;

        var extended = UnityEditorProcessController.TryExtendRestartStartupWindow(
            currentWindowDeadlineUtc,
            startupDeadlineUtc,
            new(10, new(2026, 04, 02, 12, 0, 0, TimeSpan.Zero)),
            new(11, new(2026, 04, 02, 12, 0, 1, TimeSpan.Zero)),
            out var nextWindowDeadlineUtc
        );

        await Assert.That(extended).IsTrue();
        await Assert.That(nextWindowDeadlineUtc - currentWindowDeadlineUtc).IsEqualTo(UnityToolTimeouts.RestartStartupWindow);
    }

    [Test]
    public async Task RestartStartupWindowDoesNotExtendWhenTheLogIsIdle()
    {
        var startupDeadlineUtc = DateTimeOffset.UtcNow + UnityToolTimeouts.RestartStartupMax;
        var currentWindowDeadlineUtc = DateTimeOffset.UtcNow + UnityToolTimeouts.RestartStartupWindow;
        var unchangedSnapshot = new EditorLogSnapshot(10, new(2026, 04, 02, 12, 0, 0, TimeSpan.Zero));

        var extended = UnityEditorProcessController.TryExtendRestartStartupWindow(
            currentWindowDeadlineUtc,
            startupDeadlineUtc,
            unchangedSnapshot,
            unchangedSnapshot,
            out var nextWindowDeadlineUtc
        );

        await Assert.That(extended).IsFalse();
        await Assert.That(nextWindowDeadlineUtc).IsEqualTo(currentWindowDeadlineUtc);
    }

    [Test]
    public async Task RestartStartupWindowHonorsTheHardDeadline()
    {
        var startupDeadlineUtc = DateTimeOffset.UtcNow + UnityToolTimeouts.RestartStartupMax;
        var currentWindowDeadlineUtc = startupDeadlineUtc - TimeSpan.FromSeconds(10);

        var extended = UnityEditorProcessController.TryExtendRestartStartupWindow(
            currentWindowDeadlineUtc,
            startupDeadlineUtc,
            new(10, new(2026, 04, 02, 12, 0, 0, TimeSpan.Zero)),
            new(12, new(2026, 04, 02, 12, 0, 2, TimeSpan.Zero)),
            out var nextWindowDeadlineUtc
        );

        await Assert.That(extended).IsTrue();
        await Assert.That(nextWindowDeadlineUtc).IsEqualTo(startupDeadlineUtc);
    }

    static Process StartShortLivedProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return Process.Start(
                new ProcessStartInfo("cmd", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            )!;
        }

        return Process.Start(
            new ProcessStartInfo("/bin/sh", "-c true")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        )!;
    }

    static string CreateTempProject()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"conduit-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectPath, "Temp"));
        return projectPath;
    }
}
