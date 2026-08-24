using System.Runtime.InteropServices;

namespace Conduit;

static class WaylandCompositorDiscovery
{
    static readonly string[] StandardExecutableDirectories =
    [
        "/run/current-system/sw/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
    ];

    internal static string? ResolveXdgRuntimeDirectory(string? preferredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
            return preferredDirectory;

        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } environmentDirectory)
            return environmentDirectory;

        if (!OperatingSystem.IsLinux())
            return null;

        var defaultDirectory = $"/run/user/{getuid()}";
        return Directory.Exists(defaultDirectory) ? defaultDirectory : null;
    }

    internal static string ResolveExecutablePath(string executableName, string? primaryPath, string? fallbackPath)
    {
        foreach (var directory in EnumeratePathDirectories(primaryPath))
            if (TryResolveExecutablePath(directory, executableName) is { } executablePath)
                return executablePath;

        foreach (var directory in EnumeratePathDirectories(fallbackPath))
            if (TryResolveExecutablePath(directory, executableName) is { } executablePath)
                return executablePath;

        foreach (var directory in StandardExecutableDirectories)
            if (TryResolveExecutablePath(directory, executableName) is { } executablePath)
                return executablePath;

        return executableName;
    }

    static IEnumerable<string> EnumeratePathDirectories(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            yield break;

        foreach (var directory in path.Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(directory))
                yield return directory;
    }

    static string? TryResolveExecutablePath(string directory, string executableName)
    {
        try
        {
            var candidate = Path.Combine(directory, executableName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryFindSwaySocket(string? xdgRuntimeDirectory, params string?[] preferredSocketPaths)
    {
        foreach (var socketPath in preferredSocketPaths)
            if (!string.IsNullOrWhiteSpace(socketPath) && Path.Exists(socketPath))
                return socketPath;

        if (string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            return null;

        try
        {
            if (!Directory.Exists(xdgRuntimeDirectory))
                return null;

            return FindNewestPath(
                Directory.EnumerateFileSystemEntries(xdgRuntimeDirectory, "sway-ipc.*.sock")
            );
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryFindNiriSocket(
        string? xdgRuntimeDirectory,
        string? waylandDisplay,
        params string?[] preferredSocketPaths
    )
    {
        foreach (var socketPath in preferredSocketPaths)
            if (!string.IsNullOrWhiteSpace(socketPath) && Path.Exists(socketPath))
                return socketPath;

        if (string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            return null;

        try
        {
            if (!Directory.Exists(xdgRuntimeDirectory))
                return null;

            var pattern = IsSimpleFileName(waylandDisplay)
                ? $"niri.{waylandDisplay}.*.sock"
                : "niri.wayland-*.sock";

            return FindNewestPath(Directory.EnumerateFileSystemEntries(xdgRuntimeDirectory, pattern));
        }
        catch
        {
            return null;
        }
    }

    static bool IsSimpleFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    internal static string? TryInferHyprlandInstanceSignature(string? xdgRuntimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            return null;

        try
        {
            var hyprlandDirectory = Path.Combine(xdgRuntimeDirectory, "hypr");
            if (!Directory.Exists(hyprlandDirectory))
                return null;

            var bestCandidate = FindNewestPath(
                Directory
                    .EnumerateDirectories(hyprlandDirectory)
                    .Where(directory => File.Exists(Path.Combine(directory, ".socket.sock")))
            );
            return bestCandidate is null ? null : Path.GetFileName(bestCandidate);
        }
        catch
        {
            return null;
        }
    }

    static string? FindNewestPath(IEnumerable<string> paths)
    {
        string? newestPath = null;
        var newestWriteTime = DateTime.MinValue;
        foreach (var path in paths)
        {
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (writeTime <= newestWriteTime)
                continue;

            newestPath = path;
            newestWriteTime = writeTime;
        }

        return newestPath;
    }

#if CONDUIT_LINUX
    [DllImport("libc")]
    static extern uint getuid();
#else
    static uint getuid() => 0;
#endif
}
