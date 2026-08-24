using System.Globalization;
using System.Runtime.InteropServices;

namespace Conduit;

static class DesktopSessionDiscovery
{
    internal static string? ResolveRuntimeDirectoryPath()
        => ResolveRuntimeDirectoryPath(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            ResolveCurrentUserId(),
            "/run/user"
        );

    internal static string? ResolveRuntimeDirectoryPath(string? configuredPath, string? currentUserId, string runUserRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
            return configuredPath;

        if (!string.IsNullOrWhiteSpace(currentUserId))
        {
            var runtimeDirectoryPath = Path.Combine(runUserRootPath, currentUserId);
            if (Directory.Exists(runtimeDirectoryPath))
                return runtimeDirectoryPath;
        }

        return ResolveAccessibleRuntimeDirectoryPath(runUserRootPath);
    }

    internal static string? ResolveAccessibleRuntimeDirectoryPath(string runUserRootPath)
    {
        try
        {
            if (!Directory.Exists(runUserRootPath))
                return null;

            foreach (var runtimeDirectoryPath in Directory.EnumerateDirectories(runUserRootPath))
            {
                if (!CanReadDirectory(runtimeDirectoryPath))
                    continue;

                if (Path.Exists(Path.Combine(runtimeDirectoryPath, "bus"))
                    || Directory.EnumerateFileSystemEntries(runtimeDirectoryPath, "wayland-*").Any())
                    return runtimeDirectoryPath;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    static bool CanReadDirectory(string directoryPath)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directoryPath).GetEnumerator();
            _ = entries.MoveNext();
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static string? ResolveCurrentUserId()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return GetCurrentUnixUserId().ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
            }
        }

        if (TryReadCurrentUserIdFromProcStatus() is { Length: > 0 } procStatusUserId)
            return procStatusUserId;

        var environmentUserId = Environment.GetEnvironmentVariable("UID");
        return IsUnixUserId(environmentUserId) ? environmentUserId : null;
    }

    [DllImport("libc", EntryPoint = "getuid", SetLastError = false)]
    static extern uint GetCurrentUnixUserId();

    static string? TryReadCurrentUserIdFromProcStatus()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                    continue;

                foreach (var value in line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IsUnixUserId(value))
                        return value;
                }

                return null;
            }
        }
        catch
        {
        }

        return null;
    }

    static bool IsUnixUserId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character))
                return false;
        }

        return true;
    }

    internal static string? ResolveX11Display(string socketDirectoryPath)
    {
        try
        {
            if (!Directory.Exists(socketDirectoryPath))
                return null;

            int? displayNumber = null;
            foreach (var socketPath in Directory.EnumerateFileSystemEntries(socketDirectoryPath, "X*"))
            {
                var fileName = Path.GetFileName(socketPath);
                if (fileName.Length <= 1 || !int.TryParse(fileName[1..], out var candidateNumber))
                    continue;

                if (displayNumber is null || candidateNumber < displayNumber)
                    displayNumber = candidateNumber;
            }

            return displayNumber is { } resolvedDisplayNumber ? $":{resolvedDisplayNumber}" : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveWaylandDisplay(string? runtimeDirectoryPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(runtimeDirectoryPath) || !Directory.Exists(runtimeDirectoryPath))
                return null;

            string? displayName = null;
            foreach (var socketPath in Directory.EnumerateFileSystemEntries(runtimeDirectoryPath, "wayland-*"))
            {
                var fileName = Path.GetFileName(socketPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                if (displayName is null || string.CompareOrdinal(fileName, displayName) < 0)
                    displayName = fileName;
            }

            return displayName;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveCurrentDesktop(string? runtimeDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectoryPath))
            return null;

        if (Directory.Exists(Path.Combine(runtimeDirectoryPath, "hypr")))
            return "Hyprland";

        try
        {
            if (Directory.Exists(runtimeDirectoryPath)
                && Directory.EnumerateFileSystemEntries(runtimeDirectoryPath, "sway-ipc.*.sock").Any())
                return "sway";
        }
        catch
        {
        }

        return null;
    }

    internal static string? ResolveNixXdgDesktopPortalDirectory(string systemProfilePath)
    {
        var portalDirectoryPath = Path.Combine(systemProfilePath, "share", "xdg-desktop-portal", "portals");
        try
        {
            return Directory.Exists(portalDirectoryPath)
                   && Directory.EnumerateFiles(portalDirectoryPath, "*.portal", SearchOption.TopDirectoryOnly).Any()
                ? portalDirectoryPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ResolveNixGioExtraModules(string systemProfilePath)
    {
        var serviceFilePath = Path.Combine(systemProfilePath, "share", "dbus-1", "services", "ca.desrt.dconf.service");
        try
        {
            if (!File.Exists(serviceFilePath))
                return null;

            foreach (var line in File.ReadLines(serviceFilePath))
            {
                if (!line.StartsWith("Exec=", StringComparison.Ordinal))
                    continue;

                var executablePath = line["Exec=".Length..].Trim();
                const string libexecSegment = "/libexec/";
                var libexecIndex = executablePath.IndexOf(libexecSegment, StringComparison.Ordinal);
                if (libexecIndex <= 0)
                    return null;

                var moduleDirectoryPath = Path.Combine(executablePath[..libexecIndex], "lib", "gio", "modules");
                if (Directory.Exists(moduleDirectoryPath))
                    return moduleDirectoryPath;

                return null;
            }
        }
        catch
        {
        }

        return null;
    }

    internal static string? ResolveSessionBusAddress(string? runtimeDirectoryPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeDirectoryPath))
            return null;

        var busPath = Path.Combine(runtimeDirectoryPath, "bus");
        return Path.Exists(busPath) ? $"unix:path={busPath}" : null;
    }

    internal static string? ResolveXAuthorityPath()
    {
        if (Environment.GetEnvironmentVariable("XAUTHORITY") is { Length: > 0 } configuredPath
            && Path.Exists(configuredPath))
            return configuredPath;

        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homePath))
            return null;

        var xAuthorityPath = Path.Combine(homePath, ".Xauthority");
        return Path.Exists(xAuthorityPath) ? xAuthorityPath : null;
    }
}
