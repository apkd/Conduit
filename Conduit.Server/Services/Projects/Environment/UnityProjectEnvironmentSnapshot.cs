namespace Conduit;

sealed class UnityProjectEnvironmentSnapshot(
    string projectPath,
    bool isUnityProject,
    string? editorVersion,
    UnityProjectLockfileState lockfileState,
    int runningUnityProcessCount,
    UnityProjectProcessInfo? matchedProcess)
{
    internal string ProjectPath { get; } = projectPath;
    internal bool IsUnityProject { get; } = isUnityProject;
    internal string? EditorVersion { get; } = editorVersion;
    internal UnityProjectLockfileState LockfileState { get; } = lockfileState;
    internal int RunningUnityProcessCount { get; } = runningUnityProcessCount;
    internal UnityProjectProcessInfo? MatchedProcess { get; } = matchedProcess;
    internal bool HasIdleCloseMarker { get; init; }
    internal bool WasClosedAfterIdle => HasIdleCloseMarker && IsUnityProject
        && MatchedProcess is null && LockfileState != UnityProjectLockfileState.Locked;
}
