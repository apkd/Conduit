using System.Runtime.InteropServices;

namespace Conduit;

static partial class ConduitIpcPaths
{
    const string RelativeStatePath = "conduit/ipc/v1";
    static readonly Lazy<string[]> discoveryRoots = new(CreateDiscoveryRoots);

    public static string[] GetDiscoveryRoots() => discoveryRoots.Value;

    static string[] CreateDiscoveryRoots()
    {
        if (Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT") is { Length: > 0 } configured)
            return [Path.GetFullPath(configured)];

        var roots = new HashSet<string>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Conduit",
                "ipc",
                "v1"
            ));
            return [.. roots];
        }

        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtimeDirectory)
            roots.Add(Path.Combine(runtimeDirectory, "conduit", "v1"));

        roots.Add(Path.Combine(Path.GetTempPath(), $"conduit-{GetUserId()}", "v1"));

        if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home)
        {
            roots.Add(Path.Combine(home, ".local", "state", RelativeStatePath));

            // this candidate is almost free and covers the common Flatpak layout without
            // making sandbox traversal part of the transport contract.
            var flatpakRoot = Path.Combine(home, ".var", "app");
            if (Directory.Exists(flatpakRoot))
                foreach (var applicationDirectory in Directory.EnumerateDirectories(flatpakRoot))
                    roots.Add(Path.Combine(applicationDirectory, ".local", "state", RelativeStatePath));
        }

        return [.. roots];
    }

    public static string GetEndpointDirectory(string root, string endpointId) =>
        Path.Combine(root, "endpoints", endpointId);

    static uint GetUserId() => OperatingSystem.IsWindows() ? 0 : getuid();

    [LibraryImport("libc")]
    internal static partial uint getuid();
}
