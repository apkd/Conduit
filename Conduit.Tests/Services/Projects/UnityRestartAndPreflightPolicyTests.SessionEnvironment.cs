using System.Diagnostics;
using System.Text;

namespace Conduit;

public sealed partial class UnityRestartAndPreflightPolicyTests
{
    [Test]
    public async Task UnityGioMitigationsKeepSessionGSettingsWhenSessionBusExists()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus";

        LaunchEnvironmentVariables.ApplyUnityLinuxGioMitigations(startInfo);

        await Assert.That(startInfo.Environment["GIO_USE_VFS"]).IsEqualTo("local");
        await Assert.That(startInfo.Environment["GTK_USE_PORTAL"]).IsEqualTo("0");
        await Assert.That(startInfo.Environment.ContainsKey("GSETTINGS_BACKEND")).IsFalse();
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
            await Assert.That(
                DesktopSessionDiscovery.ResolveRuntimeDirectoryPath(
                    configuredPath,
                    "1000",
                    runUserRootPath
                )
            )
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
            await Assert.That(
                DesktopSessionDiscovery.ResolveRuntimeDirectoryPath(
                    configuredPath: null,
                    "1000",
                    runUserRootPath
                )
            )
                .IsEqualTo(currentUserPath);
        }
        finally
        {
            Directory.Delete(runUserRootPath, recursive: true);
        }
    }

    [Test]
    public async Task RuntimeDirectoryResolverFallsBackToAccessibleGraphicalDirectory()
    {
        var runUserRootPath = Path.Combine(Path.GetTempPath(), $"conduit-run-user-{Guid.NewGuid():N}");
        var currentUserPath = Path.Combine(runUserRootPath, "1000");
        Directory.CreateDirectory(currentUserPath);
        File.WriteAllText(Path.Combine(currentUserPath, "bus"), string.Empty);
        try
        {
            await Assert.That(
                DesktopSessionDiscovery.ResolveRuntimeDirectoryPath(
                    configuredPath: null,
                    currentUserId: null,
                    runUserRootPath
                )
            )
                .IsEqualTo(currentUserPath);
        }
        finally
        {
            Directory.Delete(runUserRootPath, recursive: true);
        }
    }

    [Test]
    public async Task RuntimeDirectoryResolverFallsBackWhenResolvedUserDirectoryIsMissing()
    {
        var runUserRootPath = Path.Combine(Path.GetTempPath(), $"conduit-run-user-{Guid.NewGuid():N}");
        var actualUserPath = Path.Combine(runUserRootPath, "1000");
        Directory.CreateDirectory(actualUserPath);
        File.WriteAllText(Path.Combine(actualUserPath, "bus"), string.Empty);
        try
        {
            await Assert.That(
                DesktopSessionDiscovery.ResolveRuntimeDirectoryPath(
                    configuredPath: null,
                    "0",
                    runUserRootPath
                )
            )
                .IsEqualTo(actualUserPath);
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
            await Assert.That(
                DesktopSessionDiscovery.ResolveRuntimeDirectoryPath(
                    configuredPath: null,
                    "1000",
                    runUserRootPath
                )
            )
                .IsNull();
        }
        finally
        {
            Directory.Delete(runUserRootPath, recursive: true);
        }
    }

    [Test]
    public async Task CurrentUserIdResolverUsesNativeLinuxUserId()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var userId = DesktopSessionDiscovery.ResolveCurrentUserId();

        await Assert.That(string.IsNullOrWhiteSpace(userId)).IsFalse();
        await Assert.That(userId!.All(char.IsAsciiDigit)).IsTrue();
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

            await Assert.That(DesktopSessionDiscovery.ResolveX11Display(socketDirectoryPath)).IsEqualTo(":0");
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

            await Assert.That(
                DesktopSessionDiscovery.ResolveWaylandDisplay(runtimeDirectoryPath)
            ).IsEqualTo("wayland-1");
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

            await Assert.That(
                DesktopSessionDiscovery.ResolveSessionBusAddress(runtimeDirectoryPath)
            ).IsEqualTo($"unix:path={busPath}");
        }
        finally
        {
            Directory.Delete(runtimeDirectoryPath, recursive: true);
        }
    }

}
