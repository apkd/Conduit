using System.Diagnostics;

namespace Conduit;

static class LaunchEnvironmentVariables
{
    internal static void ApplyUnityLinuxGioMitigations(ProcessStartInfo startInfo)
    {
        SetIfMissing(startInfo, "GIO_USE_VFS", "local");
        SetIfMissing(startInfo, "GTK_USE_PORTAL", "0");
        if (!startInfo.Environment.TryGetValue("DBUS_SESSION_BUS_ADDRESS", out var sessionBusAddress)
            || string.IsNullOrWhiteSpace(sessionBusAddress))
            SetIfMissing(startInfo, "GSETTINGS_BACKEND", "memory");
    }

    internal static void SetIfMissing(ProcessStartInfo startInfo, string variableName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (startInfo.Environment.TryGetValue(variableName, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
            return;

        startInfo.Environment[variableName] = value;
    }

    internal static void MergePathList(
        ProcessStartInfo startInfo,
        string variableName,
        string? value,
        params string[] insertBefore
    )
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!startInfo.Environment.TryGetValue(variableName, out var existingValue) || string.IsNullOrWhiteSpace(existingValue))
        {
            startInfo.Environment[variableName] = value;
            return;
        }

        startInfo.Environment[variableName] = MergePathList(existingValue, value, insertBefore);
    }

    internal static string MergePathList(string existingValue, string value, params string[] insertBefore)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var existingPaths = EnumerateEnvironmentPaths(existingValue)
            .Where(path => seen.Add(path))
            .ToArray();
        var addedPaths = EnumerateEnvironmentPaths(value)
            .Where(path => seen.Add(path))
            .ToArray();
        if (addedPaths.Length == 0)
            return string.Join(Path.PathSeparator, existingPaths);

        var insertionPoints = insertBefore.ToHashSet(StringComparer.Ordinal);
        var result = new List<string>(existingPaths.Length + addedPaths.Length);
        var inserted = false;
        foreach (var path in existingPaths)
        {
            if (!inserted && insertionPoints.Contains(path))
            {
                result.AddRange(addedPaths);
                inserted = true;
            }

            result.Add(path);
        }

        if (!inserted)
            result.AddRange(addedPaths);

        return string.Join(Path.PathSeparator, result);
    }

    static string[] EnumerateEnvironmentPaths(string value) =>
        value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string? Get(ProcessStartInfo startInfo, string variableName) =>
        startInfo.Environment.TryGetValue(variableName, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
