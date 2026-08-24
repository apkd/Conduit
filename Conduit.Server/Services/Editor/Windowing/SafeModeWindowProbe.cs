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
        TryReadSafeModeWindowTitle(WaylandWindowTitleParser.ReadHyprlandClientsWindowTitles(json, processId));

    internal static string? TryReadSwayTreeSafeModeWindowSignal(string? json, int processId) =>
        TryReadSafeModeWindowTitle(WaylandWindowTitleParser.ReadSwayTreeWindowTitles(json, processId));

    internal static string? TryReadNiriWindowsSafeModeWindowSignal(string? json, int processId) =>
        TryReadSafeModeWindowTitle(WaylandWindowTitleParser.ReadNiriWindowsWindowTitles(json, processId));

    static string? TryReadSafeModeWindowTitle(IEnumerable<UnityWindowTitleSignal> titles)
    {
        foreach (var title in titles)
            if (IsSafeModeWindowTitle(title.Title))
                return title.Title;

        return null;
    }
}
