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
    public async Task NixOsLaunchPrefersUnityHubFhsEnvWrapper()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");
        const string unityHubPath = "/run/current-system/sw/bin/unityhub";
        const string unityHubFhsEnvPath = "/nix/store/hash-unityhub-fhs-env-3.16.2/bin/unityhub-fhs-env";
        const string steamRunPath = "/run/current-system/sw/bin/steam-run";
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: true,
            isNixOs: true,
            findExecutableOnPath: FindExecutableOnPath,
            readTextFile: static path => path == unityHubPath
                ? $"exec -a \"unityhub\" \"{unityHubFhsEnvPath}\" /nix/store/hash-unityhub-bin \"$@\""
                : null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(setsidPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(startInfo.ArgumentList[0]).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[1]).IsEqualTo("-c");
        await Assert.That(startInfo.ArgumentList[3]).IsEqualTo("conduit-unity-launch");
        await Assert.That(startInfo.ArgumentList[4]).IsEqualTo(unityHubFhsEnvPath);
        await Assert.That(startInfo.ArgumentList[5]).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList[6]).IsEqualTo("-projectPath");
        await Assert.That(startInfo.ArgumentList[7]).IsEqualTo(projectPath);
        await Assert.That(startInfo.ArgumentList[8]).IsEqualTo("-logFile");
        await Assert.That(startInfo.ArgumentList[9]).IsEqualTo(logPath);
        await Assert.That(startInfo.UseShellExecute).IsFalse();

        static string? FindExecutableOnPath(string executableName) =>
            executableName switch
            {
                "unityhub" => unityHubPath,
                "steam-run" => steamRunPath,
                "setsid" => setsidPath,
                "bash" => bashPath,
                _ => null,
            };
    }

    [Test]
    public async Task NixOsLaunchFallsBackToSteamRunWhenUnityHubFhsEnvIsUnavailable()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");
        const string steamRunPath = "/run/current-system/sw/bin/steam-run";
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: true,
            isNixOs: true,
            findExecutableOnPath: static executableName => executableName switch
            {
                "steam-run" => steamRunPath,
                "setsid" => setsidPath,
                "bash" => bashPath,
                _ => null,
            },
            readTextFile: static _ => null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(setsidPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(startInfo.ArgumentList[0]).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[4]).IsEqualTo(steamRunPath);
        await Assert.That(startInfo.ArgumentList[5]).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList[6]).IsEqualTo("-projectPath");
        await Assert.That(startInfo.ArgumentList[7]).IsEqualTo(projectPath);
        await Assert.That(startInfo.ArgumentList[8]).IsEqualTo("-logFile");
        await Assert.That(startInfo.ArgumentList[9]).IsEqualTo(logPath);
        await Assert.That(startInfo.UseShellExecute).IsFalse();
    }

    [Test]
    public async Task NonNixLinuxLaunchUsesDetachedEditorWithExplicitEnvironment()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");
        const string setsidPath = "/run/current-system/sw/bin/setsid";
        const string bashPath = "/run/current-system/sw/bin/bash";

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: true,
            isNixOs: false,
            findExecutableOnPath: static executableName => executableName switch
            {
                "setsid" => setsidPath,
                "bash" => bashPath,
                _ => null,
            },
            readTextFile: static _ => null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(setsidPath);
        await Assert.That(startInfo.Arguments).IsEmpty();
        await Assert.That(startInfo.ArgumentList[0]).IsEqualTo(bashPath);
        await Assert.That(startInfo.ArgumentList[1]).IsEqualTo("-c");
        await Assert.That(startInfo.ArgumentList[3]).IsEqualTo("conduit-unity-launch");
        await Assert.That(startInfo.ArgumentList[4]).IsEqualTo(editorPath);
        await Assert.That(startInfo.ArgumentList[5]).IsEqualTo("-projectPath");
        await Assert.That(startInfo.ArgumentList[6]).IsEqualTo(projectPath);
        await Assert.That(startInfo.ArgumentList[7]).IsEqualTo("-logFile");
        await Assert.That(startInfo.ArgumentList[8]).IsEqualTo(logPath);
        await Assert.That(startInfo.UseShellExecute).IsFalse();
    }

    [Test]
    public async Task NonLinuxLaunchUsesEditorDirectlyThroughShellExecute()
    {
        var editorPath = Path.Combine(Path.GetTempPath(), "Unity", "Editor", "Unity");
        var projectPath = Path.Combine(Path.GetTempPath(), "project");
        var logPath = Path.Combine(projectPath, "Logs", "Editor.log");

        var startInfo = UnityEditorProcessController.CreateLaunchStartInfo(
            editorPath,
            projectPath,
            logPath,
            isLinux: false,
            isNixOs: false,
            findExecutableOnPath: static _ => "/run/current-system/sw/bin/steam-run",
            readTextFile: static _ => null
        );

        await Assert.That(startInfo.FileName).IsEqualTo(editorPath);
        await Assert.That(startInfo.Arguments).IsEqualTo(UnityEditorProcessController.BuildLaunchArguments(projectPath, logPath));
        await Assert.That(startInfo.UseShellExecute).IsTrue();
    }

    [Test]
    public async Task GraphicalSessionEnvironmentDoesNotOverwriteExistingValues()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["DISPLAY"] = ":99";
        startInfo.Environment["XDG_RUNTIME_DIR"] = "/tmp/conduit-existing-runtime";
        startInfo.Environment["WAYLAND_DISPLAY"] = "wayland-existing";
        startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/tmp/conduit-existing-bus";
        startInfo.Environment["XAUTHORITY"] = "/tmp/conduit-existing-xauthority";
        startInfo.Environment["GIO_EXTRA_MODULES"] = "/tmp/conduit-existing-gio-modules";
        startInfo.Environment["GIO_USE_VFS"] = "gvfs";
        startInfo.Environment["GTK_USE_PORTAL"] = "1";
        startInfo.Environment["GSETTINGS_BACKEND"] = "dconf";

        UnityEditorProcessController.ApplyGraphicalSessionEnvironment(startInfo);

        await Assert.That(startInfo.Environment["DISPLAY"]).IsEqualTo(":99");
        await Assert.That(startInfo.Environment["XDG_RUNTIME_DIR"]).IsEqualTo("/tmp/conduit-existing-runtime");
        await Assert.That(startInfo.Environment["WAYLAND_DISPLAY"]).IsEqualTo("wayland-existing");
        await Assert.That(startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"]).IsEqualTo("unix:path=/tmp/conduit-existing-bus");
        await Assert.That(startInfo.Environment["XAUTHORITY"]).IsEqualTo("/tmp/conduit-existing-xauthority");
        await Assert.That(startInfo.Environment["GIO_EXTRA_MODULES"]).IsEqualTo("/tmp/conduit-existing-gio-modules");
        await Assert.That(startInfo.Environment["GIO_USE_VFS"]).IsEqualTo("gvfs");
        await Assert.That(startInfo.Environment["GTK_USE_PORTAL"]).IsEqualTo("1");
        await Assert.That(startInfo.Environment["GSETTINGS_BACKEND"]).IsEqualTo("dconf");
    }

    [Test]
    public async Task DesktopSessionDefaultsDeriveWaylandHyprlandValuesFromRuntimeDirectory()
    {
        var runtimeDirectoryPath = Path.Combine(Path.GetTempPath(), $"conduit-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(runtimeDirectoryPath, "hypr"));
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        try
        {
            UnityEditorProcessController.ApplyDesktopSessionDefaults(startInfo, runtimeDirectoryPath, "wayland-1", ":0");

            await Assert.That(startInfo.Environment["GDK_BACKEND"]).IsEqualTo("wayland,x11");
            await Assert.That(startInfo.Environment["NIXOS_OZONE_WL"]).IsEqualTo("1");
            await Assert.That(startInfo.Environment["NO_AT_BRIDGE"]).IsEqualTo("1");
            await Assert.That(startInfo.Environment["QT_QPA_PLATFORM"]).IsEqualTo("wayland;xcb");
            await Assert.That(startInfo.Environment["XDG_CURRENT_DESKTOP"]).IsEqualTo("Hyprland");
            await Assert.That(startInfo.Environment["XDG_SESSION_DESKTOP"]).IsEqualTo("Hyprland");
            await Assert.That(startInfo.Environment["XDG_SESSION_TYPE"]).IsEqualTo("wayland");
        }
        finally
        {
            Directory.Delete(runtimeDirectoryPath, recursive: true);
        }
    }

    [Test]
    public async Task NixOsGraphicalSessionEnvironmentDerivesPortalAndGioModulesFromSystemProfile()
    {
        var systemProfilePath = Path.Combine(Path.GetTempPath(), $"conduit-system-profile-{Guid.NewGuid():N}");
        var portalDirectoryPath = Path.Combine(systemProfilePath, "share", "xdg-desktop-portal", "portals");
        var serviceDirectoryPath = Path.Combine(systemProfilePath, "share", "dbus-1", "services");
        var dconfPackagePath = Path.Combine(systemProfilePath, "dconf");
        var gioModulesPath = Path.Combine(dconfPackagePath, "lib", "gio", "modules");
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        Directory.CreateDirectory(portalDirectoryPath);
        Directory.CreateDirectory(serviceDirectoryPath);
        Directory.CreateDirectory(gioModulesPath);
        File.WriteAllText(Path.Combine(portalDirectoryPath, "hyprland.portal"), string.Empty);
        File.WriteAllText(
            Path.Combine(serviceDirectoryPath, "ca.desrt.dconf.service"),
            $"[D-BUS Service]{Environment.NewLine}Name=ca.desrt.dconf{Environment.NewLine}Exec={dconfPackagePath}/libexec/dconf-service"
        );
        try
        {
            UnityEditorProcessController.ApplyNixOsGraphicalSessionEnvironment(startInfo, systemProfilePath);

            await Assert.That(startInfo.Environment["NIX_XDG_DESKTOP_PORTAL_DIR"]).IsEqualTo(portalDirectoryPath);
            await Assert.That(startInfo.Environment["GIO_EXTRA_MODULES"]).IsEqualTo(gioModulesPath);
        }
        finally
        {
            Directory.Delete(systemProfilePath, recursive: true);
        }
    }

    [Test]
    public async Task GraphicalSessionEnvironmentAppliesUnityGioMitigations()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();

        UnityEditorProcessController.ApplyGraphicalSessionEnvironment(startInfo);

        await Assert.That(startInfo.Environment["GIO_USE_VFS"]).IsEqualTo("local");
        await Assert.That(startInfo.Environment["GTK_USE_PORTAL"]).IsEqualTo("0");
        await Assert.That(startInfo.Environment["GSETTINGS_BACKEND"]).IsEqualTo("memory");
    }

    [Test]
    public async Task RuntimeDirectoryResolverUsesConfiguredDirectoryFirst()
    {
        var runUserRootPath = Path.Combine(Path.GetTempPath(), $"conduit-run-user-{Guid.NewGuid():N}");
        var configuredPath = Path.Combine(runUserRootPath, "configured");
        var currentUserPath = Path.Combine(runUserRootPath, "1000");
        Directory.CreateDirectory(configuredPath);
        Directory.CreateDirectory(currentUserPath);
        try
        {
            await Assert.That(UnityEditorProcessController.ResolveRuntimeDirectoryPath(configuredPath, "1000", runUserRootPath))
                .IsEqualTo(configuredPath);
        }
        finally
        {
            Directory.Delete(runUserRootPath, recursive: true);
        }
    }

    [Test]
    public async Task RuntimeDirectoryResolverFallsBackToCurrentUserDirectory()
    {
        var runUserRootPath = Path.Combine(Path.GetTempPath(), $"conduit-run-user-{Guid.NewGuid():N}");
        var currentUserPath = Path.Combine(runUserRootPath, "1000");
        Directory.CreateDirectory(currentUserPath);
        try
        {
            await Assert.That(UnityEditorProcessController.ResolveRuntimeDirectoryPath(configuredPath: null, "1000", runUserRootPath))
                .IsEqualTo(currentUserPath);
        }
        finally
        {
            Directory.Delete(runUserRootPath, recursive: true);
        }
    }

    [Test]
    public async Task RuntimeDirectoryResolverDoesNotUseOtherUserDirectory()
    {
        var runUserRootPath = Path.Combine(Path.GetTempPath(), $"conduit-run-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(runUserRootPath, "2000"));
        try
        {
            await Assert.That(UnityEditorProcessController.ResolveRuntimeDirectoryPath(configuredPath: null, "1000", runUserRootPath))
                .IsNull();
        }
        finally
        {
            Directory.Delete(runUserRootPath, recursive: true);
        }
    }

    [Test]
    public async Task X11DisplayResolverUsesLowestNumericSocketName()
    {
        var socketDirectoryPath = Path.Combine(Path.GetTempPath(), $"conduit-x11-{Guid.NewGuid():N}");
        Directory.CreateDirectory(socketDirectoryPath);
        try
        {
            File.WriteAllText(Path.Combine(socketDirectoryPath, "X2"), string.Empty);
            File.WriteAllText(Path.Combine(socketDirectoryPath, "X0_"), string.Empty);
            File.WriteAllText(Path.Combine(socketDirectoryPath, "X0"), string.Empty);

            await Assert.That(UnityEditorProcessController.ResolveX11Display(socketDirectoryPath)).IsEqualTo(":0");
        }
        finally
        {
            Directory.Delete(socketDirectoryPath, recursive: true);
        }
    }

    [Test]
    public async Task WaylandDisplayResolverUsesFirstSocketName()
    {
        var runtimeDirectoryPath = Path.Combine(Path.GetTempPath(), $"conduit-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectoryPath);
        try
        {
            File.WriteAllText(Path.Combine(runtimeDirectoryPath, "wayland-2"), string.Empty);
            File.WriteAllText(Path.Combine(runtimeDirectoryPath, "wayland-1"), string.Empty);

            await Assert.That(UnityEditorProcessController.ResolveWaylandDisplay(runtimeDirectoryPath)).IsEqualTo("wayland-1");
        }
        finally
        {
            Directory.Delete(runtimeDirectoryPath, recursive: true);
        }
    }

    [Test]
    public async Task SessionBusResolverUsesRuntimeBusPath()
    {
        var runtimeDirectoryPath = Path.Combine(Path.GetTempPath(), $"conduit-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runtimeDirectoryPath);
        try
        {
            var busPath = Path.Combine(runtimeDirectoryPath, "bus");
            File.WriteAllText(busPath, string.Empty);

            await Assert.That(UnityEditorProcessController.ResolveSessionBusAddress(runtimeDirectoryPath)).IsEqualTo($"unix:path={busPath}");
        }
        finally
        {
            Directory.Delete(runtimeDirectoryPath, recursive: true);
        }
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
    [Arguments(false, false, false, UnityProjectOfflinePreflight.InvalidProjectDiagnostic)]
    [Arguments(true, false, false, UnityProjectOfflinePreflight.MissingPackageDiagnostic)]
    [Arguments(true, false, true, UnityProjectOfflinePreflight.OfflineDiagnostic)]
    [Arguments(true, true, false, UnityProjectOfflinePreflight.MissingPackageDiagnostic)]
    [Arguments(true, true, true, UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic)]
    public async Task BlockedPreflightDistinguishesProjectProcessAndPackageState(
        bool isUnityProject,
        bool hasMatchedProcess,
        bool hasConduitPackageSignal,
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
            var backupDirectoryPath = Path.Combine(projectPath, "Temp", "__Backupscenes");
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
            var backupDirectoryPath = Path.Combine(projectPath, "Temp", "__Backupscenes");
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
