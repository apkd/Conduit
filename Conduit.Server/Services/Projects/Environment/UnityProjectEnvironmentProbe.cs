namespace Conduit;

static class UnityProjectEnvironmentProbe
{
    internal const string SafeModeDiagnostic = "The Unity Editor is in safe mode.";
    internal const string RefreshAssetDatabaseSafeModeDiagnostic =
        "The Unity Editor is in safe mode. (To recompile scripts in safe mode, use the `restart` tool.)";

    internal static UnityProjectEnvironmentSnapshot Inspect(string projectPath)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath);
        var projectVersionPath = Path.Combine(platformProjectPath, "ProjectSettings", "ProjectVersion.txt");
        var editorVersion = UnityProjectMetadata.TryReadEditorVersion(projectVersionPath);
        var runningUnityProcesses = UnityEditorProcessProbe.QueryUnityProcesses();
        return new(
            normalizedProjectPath,
            editorVersion != null || File.Exists(projectVersionPath),
            editorVersion,
            UnityProjectPackageProbe.InspectLockfile(Path.Combine(platformProjectPath, "Temp", "UnityLockfile")),
            runningUnityProcesses.Length,
            UnityEditorProcessProbe.FindMatchingProjectProcess(runningUnityProcesses, normalizedProjectPath)
        );
    }
}
