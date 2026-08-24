using System.Diagnostics;

namespace Conduit;

static class NixOsLaunchEnvironment
{
    internal static void ApplyGraphicalSession(ProcessStartInfo startInfo) =>
        ApplyGraphicalSession(startInfo, "/run/current-system/sw");

    internal static void ApplyGraphicalSession(ProcessStartInfo startInfo, string systemProfilePath)
    {
        ApplyXdgProfile(startInfo, systemProfilePath);
        LaunchEnvironmentVariables.SetIfMissing(
            startInfo,
            "NIX_XDG_DESKTOP_PORTAL_DIR",
            DesktopSessionDiscovery.ResolveNixXdgDesktopPortalDirectory(systemProfilePath)
        );
        LaunchEnvironmentVariables.SetIfMissing(
            startInfo,
            "GIO_EXTRA_MODULES",
            DesktopSessionDiscovery.ResolveNixGioExtraModules(systemProfilePath)
        );
    }

    internal static void ApplyXdgProfile(ProcessStartInfo startInfo, string systemProfilePath)
    {
        // Codex starts Conduit with a sparse sandbox environment, so Unity needs deterministic desktop search paths.
        LaunchEnvironmentVariables.MergePathList(
            startInfo,
            "XDG_CONFIG_DIRS",
            JoinExistingPaths(
                "/etc/xdg",
                CombineHomePath(startInfo, ".nix-profile", "etc", "xdg"),
                "/nix/profile/etc/xdg",
                CombineXdgStateHomePath(startInfo, "nix", "profile", "etc", "xdg"),
                CombineUserProfilePath(startInfo, "etc", "xdg"),
                "/nix/var/nix/profiles/default/etc/xdg",
                Path.Combine(systemProfilePath, "etc", "xdg")
            )
        );
        LaunchEnvironmentVariables.MergePathList(
            startInfo,
            "XDG_DATA_DIRS",
            JoinExistingPaths(
                CombineHomePath(startInfo, ".nix-profile", "share"),
                "/nix/profile/share",
                CombineXdgStateHomePath(startInfo, "nix", "profile", "share"),
                CombineUserProfilePath(startInfo, "share"),
                "/nix/var/nix/profiles/default/share",
                Path.Combine(systemProfilePath, "share"),
                "/usr/local/share",
                "/usr/share"
            ),
            "/usr/local/share",
            "/usr/share"
        );
        LaunchEnvironmentVariables.MergePathList(
            startInfo,
            "XCURSOR_PATH",
            JoinExistingPaths(
                CombineHomePath(startInfo, ".icons"),
                CombineXdgDataHomePath(startInfo, "icons"),
                CombineHomePath(startInfo, ".nix-profile", "share", "icons"),
                CombineHomePath(startInfo, ".nix-profile", "share", "pixmaps"),
                "/nix/profile/share/icons",
                "/nix/profile/share/pixmaps",
                CombineXdgStateHomePath(startInfo, "nix", "profile", "share", "icons"),
                CombineXdgStateHomePath(startInfo, "nix", "profile", "share", "pixmaps"),
                CombineUserProfilePath(startInfo, "share", "icons"),
                CombineUserProfilePath(startInfo, "share", "pixmaps"),
                "/nix/var/nix/profiles/default/share/icons",
                "/nix/var/nix/profiles/default/share/pixmaps",
                Path.Combine(systemProfilePath, "share", "icons"),
                Path.Combine(systemProfilePath, "share", "pixmaps")
            )
        );
    }

    static string? CombineHomePath(ProcessStartInfo startInfo, params string[] segments)
    {
        if (LaunchEnvironmentVariables.Get(startInfo, "HOME") is not { Length: > 0 } homePath)
            return null;

        return CombinePath(homePath, segments);
    }

    static string? CombineXdgDataHomePath(ProcessStartInfo startInfo, params string[] segments)
    {
        if (LaunchEnvironmentVariables.Get(startInfo, "XDG_DATA_HOME") is { Length: > 0 } dataHomePath)
            return CombinePath(dataHomePath, segments);

        return CombineHomePath(startInfo, PrependSegments(".local", "share", segments));
    }

    static string? CombineXdgStateHomePath(ProcessStartInfo startInfo, params string[] segments)
    {
        if (LaunchEnvironmentVariables.Get(startInfo, "XDG_STATE_HOME") is { Length: > 0 } stateHomePath)
            return CombinePath(stateHomePath, segments);

        return CombineHomePath(startInfo, PrependSegments(".local", "state", segments));
    }

    static string? CombineUserProfilePath(ProcessStartInfo startInfo, params string[] segments)
    {
        var userName = LaunchEnvironmentVariables.Get(startInfo, "USER")
                       ?? LaunchEnvironmentVariables.Get(startInfo, "LOGNAME")
                       ?? Environment.UserName;
        if (string.IsNullOrWhiteSpace(userName))
            return null;

        return CombinePath(Path.Combine("/etc/profiles/per-user", userName), segments);
    }

    static string CombinePath(string rootPath, string[] segments)
    {
        var paths = new string[segments.Length + 1];
        paths[0] = rootPath;
        Array.Copy(segments, 0, paths, 1, segments.Length);
        return Path.Combine(paths);
    }

    static string[] PrependSegments(string first, string second, string[] segments)
    {
        var paths = new string[segments.Length + 2];
        paths[0] = first;
        paths[1] = second;
        Array.Copy(segments, 0, paths, 2, segments.Length);
        return paths;
    }

    static string? JoinExistingPaths(params string?[] paths)
    {
        var existingPaths = new List<string>();
        foreach (var path in paths)
            if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path))
                existingPaths.Add(path);

        return existingPaths.Count > 0 ? string.Join(Path.PathSeparator, existingPaths) : null;
    }
}
