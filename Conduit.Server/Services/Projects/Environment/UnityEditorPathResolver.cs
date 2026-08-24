using System.Diagnostics;

namespace Conduit;

static class UnityEditorPathResolver
{
    internal static string? ResolveUnityEditorPath(UnityProjectEnvironmentSnapshot snapshot, Process? process)
    {
        var processPath = ProcessInspection.TryGetProcessPath(process) ?? snapshot.MatchedProcess?.ExecutablePath;
        return ResolveUnityEditorPath(
            snapshot.EditorVersion,
            processPath,
            Environment.GetEnvironmentVariable("CONDUIT_UNITY_EDITOR"),
            Environment.GetEnvironmentVariable("UNITY_EDITOR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            OperatingSystem.IsWindows(),
            OperatingSystem.IsLinux(),
            File.Exists
        );
    }

    internal static string? ResolveUnityEditorPath(
        string? editorVersion,
        string? processPath,
        string? conduitEditorOverride,
        string? unityEditorOverride,
        string userHome,
        string programFiles,
        bool isWindows,
        bool isLinux,
        Func<string, bool> fileExists
    )
    {
        foreach (var candidate in EnumerateUnityEditorPathCandidates(
                     editorVersion,
                     processPath,
                     conduitEditorOverride,
                     unityEditorOverride,
                     userHome,
                     programFiles,
                     isWindows,
                     isLinux
                 ))
            if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
                return candidate;

        return null;
    }

    static IEnumerable<string> EnumerateUnityEditorPathCandidates(
        string? editorVersion,
        string? processPath,
        string? conduitEditorOverride,
        string? unityEditorOverride,
        string userHome,
        string programFiles,
        bool isWindows,
        bool isLinux
    )
    {
        if (!string.IsNullOrWhiteSpace(processPath))
            yield return processPath;

        if (!string.IsNullOrWhiteSpace(conduitEditorOverride))
            yield return conduitEditorOverride;

        if (!string.IsNullOrWhiteSpace(unityEditorOverride))
            yield return unityEditorOverride;

        if (string.IsNullOrWhiteSpace(editorVersion))
            yield break;

        if (isWindows && !string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity.exe");
            yield return Path.Combine(programFiles, "Unity", "Editor", "Unity.exe");
        }

        if (!isLinux)
            yield break;

        yield return Path.Combine("/opt", "unity", editorVersion, "Editor", "Unity");
        yield return Path.Combine("/opt", "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");

        if (string.IsNullOrWhiteSpace(userHome))
            yield break;

        yield return Path.Combine(userHome, "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
        yield return Path.Combine(userHome, ".local", "share", "Unity", "Hub", "Editor", editorVersion, "Editor", "Unity");
    }
}
