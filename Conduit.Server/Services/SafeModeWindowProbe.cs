namespace Conduit;

static class SafeModeWindowProbe
{
    internal static bool IsSafeModeWindowTitle(string? title) =>
        UnityWindowTitleClassifier.IsSafeModeTitle(title);

    internal static string? TryReadSafeModeWindowSignal(int processId) =>
        UnityWindowTitleProbe
            .TryFindMatchingProcessWindowTitle(processId, IsSafeModeWindowTitle)
            ?.Title;

    internal static string? TryReadSafeModeWindowSignal(
        int processId,
        string? mainWindowTitle) =>
        UnityWindowTitleProbe
            .TryFindMatchingProcessWindowTitle(
                processId,
                IsSafeModeWindowTitle,
                mainWindowTitle
            )
            ?.Title;

    internal static string? TryReadHyprlandClientsSafeModeWindowSignal(string? json, int processId) =>
        TryReadSafeModeWindowTitle(UnityWindowTitleProbe.ReadHyprlandClientsWindowTitles(json, processId));

    internal static string? TryReadSwayTreeSafeModeWindowSignal(string? json, int processId) =>
        TryReadSafeModeWindowTitle(UnityWindowTitleProbe.ReadSwayTreeWindowTitles(json, processId));

    internal static string? TryReadNiriWindowsSafeModeWindowSignal(string? json, int processId) =>
        TryReadSafeModeWindowTitle(UnityWindowTitleProbe.ReadNiriWindowsWindowTitles(json, processId));

    internal static string ResolveExecutablePath(string executableName, string? primaryPath, string? fallbackPath) =>
        UnityWindowTitleProbe.ResolveExecutablePath(executableName, primaryPath, fallbackPath);

    internal static string? TryFindSwaySocket(string? xdgRuntimeDirectory, params string?[] preferredSocketPaths) =>
        UnityWindowTitleProbe.TryFindSwaySocket(xdgRuntimeDirectory, preferredSocketPaths);

    internal static string? TryFindNiriSocket(
        string? xdgRuntimeDirectory,
        string? waylandDisplay,
        params string?[] preferredSocketPaths
    ) =>
        UnityWindowTitleProbe.TryFindNiriSocket(xdgRuntimeDirectory, waylandDisplay, preferredSocketPaths);

    internal static string? TryInferHyprlandInstanceSignature(string? xdgRuntimeDirectory) =>
        UnityWindowTitleProbe.TryInferHyprlandInstanceSignature(xdgRuntimeDirectory);

    static string? TryReadSafeModeWindowTitle(IEnumerable<UnityWindowTitleSignal> titles)
    {
        foreach (var title in titles)
            if (IsSafeModeWindowTitle(title.Title))
                return title.Title;

        return null;
    }
}
