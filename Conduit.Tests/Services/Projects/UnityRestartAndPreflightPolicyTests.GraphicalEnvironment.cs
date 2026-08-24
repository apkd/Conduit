using System.Diagnostics;
using System.Text;

namespace Conduit;

public sealed partial class UnityRestartAndPreflightPolicyTests
{
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
        startInfo.Environment["GTK_THEME"] = "Existing:dark";
        startInfo.Environment["XCURSOR_THEME"] = "existing-cursor";
        startInfo.Environment["XCURSOR_SIZE"] = "64";
        startInfo.Environment["XDG_CONFIG_HOME"] = "/tmp/conduit-existing-config";
        startInfo.Environment["XDG_DATA_HOME"] = "/tmp/conduit-existing-data";
        startInfo.Environment["XDG_CACHE_HOME"] = "/tmp/conduit-existing-cache";

        UnityEditorLaunchEnvironment.ApplyGraphicalSessionEnvironment(startInfo);

        await Assert.That(startInfo.Environment["DISPLAY"]).IsEqualTo(":99");
        await Assert.That(startInfo.Environment["XDG_RUNTIME_DIR"]).IsEqualTo("/tmp/conduit-existing-runtime");
        await Assert.That(startInfo.Environment["WAYLAND_DISPLAY"]).IsEqualTo("wayland-existing");
        await Assert.That(startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"]).IsEqualTo("unix:path=/tmp/conduit-existing-bus");
        await Assert.That(startInfo.Environment["XAUTHORITY"]).IsEqualTo("/tmp/conduit-existing-xauthority");
        await Assert.That(startInfo.Environment["GIO_EXTRA_MODULES"]).IsEqualTo("/tmp/conduit-existing-gio-modules");
        await Assert.That(startInfo.Environment["GIO_USE_VFS"]).IsEqualTo("gvfs");
        await Assert.That(startInfo.Environment["GTK_USE_PORTAL"]).IsEqualTo("1");
        await Assert.That(startInfo.Environment["GSETTINGS_BACKEND"]).IsEqualTo("dconf");
        await Assert.That(startInfo.Environment["GTK_THEME"]).IsEqualTo("Existing:dark");
        await Assert.That(startInfo.Environment["XCURSOR_THEME"]).IsEqualTo("existing-cursor");
        await Assert.That(startInfo.Environment["XCURSOR_SIZE"]).IsEqualTo("64");
        await Assert.That(startInfo.Environment["XDG_CONFIG_HOME"]).IsEqualTo("/tmp/conduit-existing-config");
        await Assert.That(startInfo.Environment["XDG_DATA_HOME"]).IsEqualTo("/tmp/conduit-existing-data");
        await Assert.That(startInfo.Environment["XDG_CACHE_HOME"]).IsEqualTo("/tmp/conduit-existing-cache");
    }

    [Test]
    public async Task RestartProcessEnvironmentUsesPreviousEditorEnvironment()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["GTK_THEME"] = "wrong";
        startInfo.Environment["XDG_CONFIG_HOME"] = "/tmp/wrong-config";

        UnityEditorLaunchEnvironment.ApplyRestartProcessEnvironment(
            startInfo,
            new Dictionary<string, string>
            {
                ["HOME"] = "/home/sample",
                ["DISPLAY"] = ":7",
                ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1234/bus",
                ["GDK_BACKEND"] = "wayland",
                ["GTK_THEME"] = "SampleTheme:dark",
                ["QT_QPA_PLATFORM"] = "wayland;xcb",
                ["WAYLAND_DISPLAY"] = "wayland-7",
                ["XCURSOR_SIZE"] = "48",
                ["XCURSOR_THEME"] = "sample-cursor",
                ["XDG_CACHE_HOME"] = "/home/sample/.cache",
                ["XDG_CONFIG_HOME"] = "/home/sample/.config",
                ["XDG_CURRENT_DESKTOP"] = "SampleDesktop",
                ["XDG_DATA_HOME"] = "/home/sample/.local/share",
                ["XDG_RUNTIME_DIR"] = "/run/user/1234",
                ["XDG_SESSION_DESKTOP"] = "SampleDesktop",
                ["XDG_SESSION_TYPE"] = "wayland",
            }
        );

        await Assert.That(startInfo.Environment["HOME"]).IsEqualTo("/home/sample");
        await Assert.That(startInfo.Environment["DISPLAY"]).IsEqualTo(":7");
        await Assert.That(startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"]).IsEqualTo("unix:path=/run/user/1234/bus");
        await Assert.That(startInfo.Environment["GDK_BACKEND"]).IsEqualTo("x11");
        await Assert.That(startInfo.Environment["GTK_THEME"]).IsEqualTo("SampleTheme:dark");
        await Assert.That(startInfo.Environment["QT_QPA_PLATFORM"]).IsEqualTo("wayland;xcb");
        await Assert.That(startInfo.Environment["WAYLAND_DISPLAY"]).IsEqualTo("wayland-7");
        await Assert.That(startInfo.Environment["XCURSOR_SIZE"]).IsEqualTo("48");
        await Assert.That(startInfo.Environment["XCURSOR_THEME"]).IsEqualTo("sample-cursor");
        await Assert.That(startInfo.Environment["XDG_CACHE_HOME"]).IsEqualTo("/home/sample/.cache");
        await Assert.That(startInfo.Environment["XDG_CONFIG_HOME"]).IsEqualTo("/home/sample/.config");
        await Assert.That(startInfo.Environment["XDG_CURRENT_DESKTOP"]).IsEqualTo("SampleDesktop");
        await Assert.That(startInfo.Environment["XDG_DATA_HOME"]).IsEqualTo("/home/sample/.local/share");
        await Assert.That(startInfo.Environment["XDG_RUNTIME_DIR"]).IsEqualTo("/run/user/1234");
        await Assert.That(startInfo.Environment["XDG_SESSION_DESKTOP"]).IsEqualTo("SampleDesktop");
        await Assert.That(startInfo.Environment["XDG_SESSION_TYPE"]).IsEqualTo("wayland");
        await Assert.That(startInfo.Environment.ContainsKey("GSETTINGS_BACKEND")).IsFalse();
        await Assert.That(startInfo.Environment["GIO_USE_VFS"]).IsEqualTo("local");
        await Assert.That(startInfo.Environment["GTK_USE_PORTAL"]).IsEqualTo("0");
    }

    [Test]
    public async Task ProcessEnvironmentParserReadsNullSeparatedEntries()
    {
        var environment = UnityEditorLaunchEnvironment.ParseProcessEnvironment(
            Encoding.UTF8.GetBytes("GTK_THEME=Sample:dark\0BROKEN\0XCURSOR_SIZE=32\0A=B=C\0")
        );

        await Assert.That(environment["GTK_THEME"]).IsEqualTo("Sample:dark");
        await Assert.That(environment["XCURSOR_SIZE"]).IsEqualTo("32");
        await Assert.That(environment["A"]).IsEqualTo("B=C");
        await Assert.That(environment.ContainsKey("BROKEN")).IsFalse();
    }

    [Test]
    public async Task XdgBaseDirectoryDefaultsUseHomeDirectory()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = "/home/sample";

        UnityEditorLaunchEnvironment.ApplyXdgBaseDirectoryDefaults(startInfo);

        await Assert.That(startInfo.Environment["XDG_CONFIG_HOME"]).IsEqualTo(Path.Combine("/home/sample", ".config"));
        await Assert.That(startInfo.Environment["XDG_DATA_HOME"]).IsEqualTo(Path.Combine("/home/sample", ".local", "share"));
        await Assert.That(startInfo.Environment["XDG_CACHE_HOME"]).IsEqualTo(Path.Combine("/home/sample", ".cache"));
        await Assert.That(startInfo.Environment["XDG_STATE_HOME"]).IsEqualTo(Path.Combine("/home/sample", ".local", "state"));
        await Assert.That(startInfo.Environment["XDG_CONFIG_DIRS"]).IsEqualTo("/etc/xdg");
        await Assert.That(startInfo.Environment["XDG_DATA_DIRS"]).IsEqualTo("/usr/local/share:/usr/share");
    }

    [Test]
    public async Task GtkUserSettingsEnvironmentDerivesThemeAndCursorVariables()
    {
        var homePath = Path.Combine(Path.GetTempPath(), $"conduit-home-{Guid.NewGuid():N}");
        var settingsDirectoryPath = Path.Combine(homePath, ".config", "gtk-3.0");
        Directory.CreateDirectory(settingsDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectoryPath, "settings.ini"),
            """
            [Settings]
            gtk-theme-name=Example-dark
            gtk-application-prefer-dark-theme=true
            gtk-cursor-theme-name=example-cursor
            gtk-cursor-theme-size=32
            """
        );

        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = homePath;
        try
        {
            UnityEditorLaunchEnvironment.ApplyXdgBaseDirectoryDefaults(startInfo);
            GtkLaunchEnvironment.Apply(startInfo);

            await Assert.That(startInfo.Environment["GTK_THEME"]).IsEqualTo("Example:dark");
            await Assert.That(startInfo.Environment["XCURSOR_THEME"]).IsEqualTo("example-cursor");
            await Assert.That(startInfo.Environment["XCURSOR_SIZE"]).IsEqualTo("32");
        }
        finally
        {
            Directory.Delete(homePath, recursive: true);
        }
    }

    [Test]
    public async Task GtkUserSettingsEnvironmentKeepsInstalledThemeName()
    {
        var homePath = Path.Combine(Path.GetTempPath(), $"conduit-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(homePath, ".themes", "Example-dark"));
        var settingsDirectoryPath = Path.Combine(homePath, ".config", "gtk-3.0");
        Directory.CreateDirectory(settingsDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectoryPath, "settings.ini"),
            """
            [Settings]
            gtk-theme-name=Example-dark
            gtk-application-prefer-dark-theme=true
            """
        );

        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = homePath;
        try
        {
            UnityEditorLaunchEnvironment.ApplyXdgBaseDirectoryDefaults(startInfo);
            GtkLaunchEnvironment.Apply(startInfo);

            await Assert.That(startInfo.Environment["GTK_THEME"]).IsEqualTo("Example-dark");
        }
        finally
        {
            Directory.Delete(homePath, recursive: true);
        }
    }

    [Test]
    public async Task DesktopSessionDefaultsPreferGtkX11WhenXDisplayExists()
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
            UnityEditorLaunchEnvironment.ApplyDesktopSessionDefaults(startInfo, runtimeDirectoryPath, "wayland-1", ":0");

            await Assert.That(startInfo.Environment["GDK_BACKEND"]).IsEqualTo("x11");
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
    public async Task DesktopSessionDefaultsUseGtkX11ForX11OnlySession()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();

        UnityEditorLaunchEnvironment.ApplyDesktopSessionDefaults(startInfo, runtimeDirectoryPath: null, waylandDisplay: null, ":0");

        await Assert.That(startInfo.Environment["GDK_BACKEND"]).IsEqualTo("x11");
        await Assert.That(startInfo.Environment["QT_QPA_PLATFORM"]).IsEqualTo("xcb");
        await Assert.That(startInfo.Environment["XDG_SESSION_TYPE"]).IsEqualTo("x11");
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
            NixOsLaunchEnvironment.ApplyGraphicalSession(startInfo, systemProfilePath);

            await Assert.That(startInfo.Environment["NIX_XDG_DESKTOP_PORTAL_DIR"]).IsEqualTo(portalDirectoryPath);
            await Assert.That(startInfo.Environment["GIO_EXTRA_MODULES"]).IsEqualTo(gioModulesPath);
        }
        finally
        {
            Directory.Delete(systemProfilePath, recursive: true);
        }
    }

    [Test]
    public async Task NixOsXdgProfileEnvironmentAddsDesktopSearchPathsFromProfiles()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"conduit-nix-profile-{Guid.NewGuid():N}");
        var homePath = Path.Combine(rootPath, "home");
        var systemProfilePath = Path.Combine(rootPath, "system-profile");
        var homeProfileSharePath = Path.Combine(homePath, ".nix-profile", "share");
        var homeProfileConfigPath = Path.Combine(homePath, ".nix-profile", "etc", "xdg");
        var stateProfileSharePath = Path.Combine(homePath, ".local", "state", "nix", "profile", "share");
        var stateProfileConfigPath = Path.Combine(homePath, ".local", "state", "nix", "profile", "etc", "xdg");
        var systemSharePath = Path.Combine(systemProfilePath, "share");
        var systemConfigPath = Path.Combine(systemProfilePath, "etc", "xdg");
        Directory.CreateDirectory(homeProfileSharePath);
        Directory.CreateDirectory(homeProfileConfigPath);
        Directory.CreateDirectory(stateProfileSharePath);
        Directory.CreateDirectory(stateProfileConfigPath);
        Directory.CreateDirectory(Path.Combine(homePath, ".icons"));
        Directory.CreateDirectory(Path.Combine(homePath, ".local", "share", "icons"));
        Directory.CreateDirectory(Path.Combine(systemSharePath, "icons"));
        Directory.CreateDirectory(Path.Combine(systemSharePath, "pixmaps"));
        Directory.CreateDirectory(systemConfigPath);

        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = homePath;
        startInfo.Environment["USER"] = "sample";
        startInfo.Environment["XDG_DATA_DIRS"] = string.Join(Path.PathSeparator, "/usr/local/share", "/usr/share");
        startInfo.Environment["XDG_CONFIG_DIRS"] = "/etc/xdg";
        try
        {
            NixOsLaunchEnvironment.ApplyXdgProfile(startInfo, systemProfilePath);

            var dataPaths = SplitEnvironmentPaths(startInfo.Environment["XDG_DATA_DIRS"]!);
            var configPaths = SplitEnvironmentPaths(startInfo.Environment["XDG_CONFIG_DIRS"]!);
            var cursorPaths = SplitEnvironmentPaths(startInfo.Environment["XCURSOR_PATH"]!);

            await Assert.That(dataPaths.Contains(homeProfileSharePath)).IsTrue();
            await Assert.That(dataPaths.Contains(stateProfileSharePath)).IsTrue();
            await Assert.That(dataPaths.Contains(systemSharePath)).IsTrue();
            await Assert.That(Array.IndexOf(dataPaths, systemSharePath) < Array.IndexOf(dataPaths, "/usr/share")).IsTrue();
            await Assert.That(configPaths.Contains(homeProfileConfigPath)).IsTrue();
            await Assert.That(configPaths.Contains(stateProfileConfigPath)).IsTrue();
            await Assert.That(configPaths.Contains(systemConfigPath)).IsTrue();
            await Assert.That(cursorPaths.Contains(Path.Combine(homePath, ".icons"))).IsTrue();
            await Assert.That(cursorPaths.Contains(Path.Combine(homePath, ".local", "share", "icons"))).IsTrue();
            await Assert.That(cursorPaths.Contains(Path.Combine(systemSharePath, "icons"))).IsTrue();
            await Assert.That(cursorPaths.Contains(Path.Combine(systemSharePath, "pixmaps"))).IsTrue();
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Test]
    public async Task NixOsXdgProfileEnvironmentKeepsSystemProfileThemeNames()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"conduit-nix-theme-{Guid.NewGuid():N}");
        var homePath = Path.Combine(rootPath, "home");
        var systemProfilePath = Path.Combine(rootPath, "system-profile");
        Directory.CreateDirectory(Path.Combine(systemProfilePath, "share", "themes", "Example-dark"));
        var settingsDirectoryPath = Path.Combine(homePath, ".config", "gtk-3.0");
        Directory.CreateDirectory(settingsDirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(settingsDirectoryPath, "settings.ini"),
            """
            [Settings]
            gtk-theme-name=Example-dark
            gtk-application-prefer-dark-theme=true
            """
        );

        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();
        startInfo.Environment["HOME"] = homePath;
        try
        {
            UnityEditorLaunchEnvironment.ApplyXdgBaseDirectoryDefaults(startInfo);
            NixOsLaunchEnvironment.ApplyXdgProfile(startInfo, systemProfilePath);
            GtkLaunchEnvironment.Apply(startInfo);

            await Assert.That(startInfo.Environment["GTK_THEME"]).IsEqualTo("Example-dark");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Test]
    public async Task EnvironmentPathListMergeInsertsNixPathsBeforeSystemFallbacks()
    {
        var merged = LaunchEnvironmentVariables.MergePathList(
            string.Join(Path.PathSeparator, "/custom/share", "/usr/local/share", "/usr/share"),
            string.Join(Path.PathSeparator, "/nix/profile/share", "/run/current-system/sw/share", "/usr/share"),
            "/usr/local/share",
            "/usr/share"
        );

        await Assert.That(merged).IsEqualTo(
            string.Join(
                Path.PathSeparator,
                "/custom/share",
                "/nix/profile/share",
                "/run/current-system/sw/share",
                "/usr/local/share",
                "/usr/share"
            )
        );
    }

    static string[] SplitEnvironmentPaths(string value) =>
        value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Test]
    public async Task GraphicalSessionEnvironmentAppliesUnityGioMitigations()
    {
        var startInfo = new ProcessStartInfo("Unity")
        {
            UseShellExecute = false,
        };
        startInfo.Environment.Clear();

        LaunchEnvironmentVariables.ApplyUnityLinuxGioMitigations(startInfo);

        await Assert.That(startInfo.Environment["GIO_USE_VFS"]).IsEqualTo("local");
        await Assert.That(startInfo.Environment["GTK_USE_PORTAL"]).IsEqualTo("0");
        await Assert.That(startInfo.Environment["GSETTINGS_BACKEND"]).IsEqualTo("memory");
    }

}
