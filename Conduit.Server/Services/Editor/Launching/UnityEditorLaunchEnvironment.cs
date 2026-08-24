using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Conduit;

static class UnityEditorLaunchEnvironment
{
    internal const string RestartStartedUtcTicksEnvironmentVariable =
        "CONDUIT_RESTART_STARTED_UTC_TICKS";

    internal static void ApplyGraphicalSessionEnvironment(ProcessStartInfo startInfo)
    {
        ApplyXdgBaseDirectoryDefaults(startInfo);

        var runtimeDirectoryPath = DesktopSessionDiscovery.ResolveRuntimeDirectoryPath();
        var waylandDisplay = DesktopSessionDiscovery.ResolveWaylandDisplay(runtimeDirectoryPath);
        var x11Display = DesktopSessionDiscovery.ResolveX11Display("/tmp/.X11-unix");

        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_RUNTIME_DIR", runtimeDirectoryPath);
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "DISPLAY", x11Display);
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "WAYLAND_DISPLAY", waylandDisplay);
        LaunchEnvironmentVariables.SetIfMissing(
            startInfo,
            "DBUS_SESSION_BUS_ADDRESS",
            DesktopSessionDiscovery.ResolveSessionBusAddress(runtimeDirectoryPath)
        );
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XAUTHORITY", DesktopSessionDiscovery.ResolveXAuthorityPath());

        ApplyDesktopSessionDefaults(
            startInfo,
            LaunchEnvironmentVariables.Get(startInfo, "XDG_RUNTIME_DIR") ?? runtimeDirectoryPath,
            LaunchEnvironmentVariables.Get(startInfo, "WAYLAND_DISPLAY") ?? waylandDisplay,
            LaunchEnvironmentVariables.Get(startInfo, "DISPLAY") ?? x11Display
        );
        NixOsLaunchEnvironment.ApplyGraphicalSession(startInfo);
        GtkLaunchEnvironment.Apply(startInfo);
        LaunchEnvironmentVariables.ApplyUnityLinuxGioMitigations(startInfo);
    }

    internal static void ApplyRestartProcessEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? editorEnvironment
    )
    {
        if (startInfo.UseShellExecute)
            return;

        if (editorEnvironment is { Count: > 0 })
        {
            startInfo.Environment.Clear();
            foreach (var (variableName, value) in editorEnvironment)
                if (IsValidEnvironmentVariableName(variableName))
                    startInfo.Environment[variableName] = value;
        }

        ApplyGraphicalSessionEnvironment(startInfo);
    }

    internal static void ApplyRestartUsageTracking(
        ProcessStartInfo startInfo,
        long? startedUtcTicks
    )
    {
        // captured editor environments may contain an earlier restart marker.
        startInfo.Environment.Remove(RestartStartedUtcTicksEnvironmentVariable);
        if (startedUtcTicks is { } value)
            startInfo.Environment[RestartStartedUtcTicksEnvironmentVariable] =
                value.ToString(CultureInfo.InvariantCulture);
    }

    static bool IsValidEnvironmentVariableName(string variableName) =>
        !string.IsNullOrWhiteSpace(variableName)
        && !variableName.Contains('=', StringComparison.Ordinal)
        && !variableName.Contains('\0', StringComparison.Ordinal);

    internal static IReadOnlyDictionary<string, string>? TryReadProcessEnvironment(int processId)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            return ParseProcessEnvironment(File.ReadAllBytes($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/environ"));
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> ParseProcessEnvironment(byte[] bytes)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = entry.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            environment[entry[..separatorIndex]] = entry[(separatorIndex + 1)..];
        }

        return environment;
    }

    internal static void ApplyDesktopSessionDefaults(
        ProcessStartInfo startInfo,
        string? runtimeDirectoryPath,
        string? waylandDisplay,
        string? x11Display
    )
    {
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
        {
            LaunchEnvironmentVariables.SetIfMissing(startInfo, "NIXOS_OZONE_WL", "1");
            LaunchEnvironmentVariables.SetIfMissing(startInfo, "QT_QPA_PLATFORM", "wayland;xcb");
            LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_SESSION_TYPE", "wayland");
        }
        else if (!string.IsNullOrWhiteSpace(x11Display))
        {
            LaunchEnvironmentVariables.SetIfMissing(startInfo, "QT_QPA_PLATFORM", "xcb");
            LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_SESSION_TYPE", "x11");
        }

        ApplyUnityEditorGtkBackend(startInfo, x11Display, waylandDisplay);
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "NO_AT_BRIDGE", "1");

        var currentDesktop = DesktopSessionDiscovery.ResolveCurrentDesktop(runtimeDirectoryPath);
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_CURRENT_DESKTOP", currentDesktop);
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_SESSION_DESKTOP", currentDesktop);
    }

    static void ApplyUnityEditorGtkBackend(ProcessStartInfo startInfo, string? x11Display, string? waylandDisplay)
    {
        // Unity's Linux editor commonly runs through XWayland, so GTK clipboard calls need the X11 backend when DISPLAY is present.
        if (!string.IsNullOrWhiteSpace(x11Display))
        {
            startInfo.Environment["GDK_BACKEND"] = "x11";
            return;
        }

        if (!string.IsNullOrWhiteSpace(waylandDisplay))
            startInfo.Environment["GDK_BACKEND"] = "wayland,x11";
    }

    internal static void ApplyXdgBaseDirectoryDefaults(ProcessStartInfo startInfo)
    {
        var homePath = ResolveHomePath(startInfo);
        if (string.IsNullOrWhiteSpace(homePath))
            return;

        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_CONFIG_HOME", Path.Combine(homePath, ".config"));
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_DATA_HOME", Path.Combine(homePath, ".local", "share"));
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_CACHE_HOME", Path.Combine(homePath, ".cache"));
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_STATE_HOME", Path.Combine(homePath, ".local", "state"));
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_CONFIG_DIRS", "/etc/xdg");
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XDG_DATA_DIRS", "/usr/local/share:/usr/share");
    }

    static string? ResolveHomePath(ProcessStartInfo startInfo)
    {
        if (startInfo.Environment.TryGetValue("HOME", out var configuredHomePath)
            && !string.IsNullOrWhiteSpace(configuredHomePath))
            return configuredHomePath;

        var environmentHomePath = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(environmentHomePath))
        {
            LaunchEnvironmentVariables.SetIfMissing(startInfo, "HOME", environmentHomePath);
            return environmentHomePath;
        }

        var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profilePath))
            return null;

        LaunchEnvironmentVariables.SetIfMissing(startInfo, "HOME", profilePath);
        return profilePath;
    }
}
