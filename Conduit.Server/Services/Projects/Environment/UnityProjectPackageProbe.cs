using System.Text.Json;

namespace Conduit;

static class UnityProjectPackageProbe
{
    internal static bool HasConduitPackageSignal(string projectPath)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        if (string.IsNullOrWhiteSpace(normalizedProjectPath))
            return false;

        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath);
        if (File.Exists(Path.Combine(platformProjectPath, "Packages", "dev.tryfinally.conduit", "package.json")))
            return true;

        return PackageFileContainsConduitDependency(Path.Combine(platformProjectPath, "Packages", "manifest.json"))
               || PackageFileContainsConduitDependency(Path.Combine(platformProjectPath, "Packages", "packages-lock.json"));
    }

    static bool PackageFileContainsConduitDependency(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("dependencies", out var dependencies)
                   && dependencies.ValueKind == JsonValueKind.Object
                   && dependencies.TryGetProperty("dev.tryfinally.conduit", out _);
        }
        catch
        {
            return false;
        }
    }

    internal static UnityProjectLockfileState InspectLockfile(string lockfilePath)
    {
        if (!File.Exists(lockfilePath))
            return UnityProjectLockfileState.Missing;

        try
        {
            using var stream = new FileStream(lockfilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return UnityProjectLockfileState.Stale;
        }
        catch (IOException)
        {
            return UnityProjectLockfileState.Locked;
        }
        catch (UnauthorizedAccessException)
        {
            return UnityProjectLockfileState.Locked;
        }
    }
}
