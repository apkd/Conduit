namespace Conduit;

static class UnityWindowTitleProbe
{
    internal static UnityWindowTitleSignal? TryFindMatchingProcessWindowTitle(int processId, Func<string, bool> predicate)
        => TryFindMatchingProcessWindowTitle(
            processId,
            predicate,
            TryReadMainWindowTitle(processId)
        );

    internal static UnityWindowTitleSignal? TryFindMatchingProcessWindowTitle(
        int processId,
        Func<string, bool> predicate,
        string? mainWindowTitle)
    {
        if (processId <= 0)
            return null;

        if (mainWindowTitle is not null && predicate(mainWindowTitle))
            return new(mainWindowTitle, false, "process");

        foreach (var signal in ReadProcessWindowTitles(processId, mainWindowTitle))
            if (predicate(signal.Title))
                return signal;

        return null;
    }

    internal static List<UnityWindowTitleSignal> ReadProcessWindowTitles(int processId) =>
        ReadProcessWindowTitles(processId, TryReadMainWindowTitle(processId));

    static List<UnityWindowTitleSignal> ReadProcessWindowTitles(
        int processId,
        string? mainWindowTitle)
    {
        var titles = new List<UnityWindowTitleSignal>(4);
        if (processId <= 0)
            return titles;

        // keep the cheap process api first; platform probes fill in secondary modal/progress windows.
        if (mainWindowTitle is not null)
            AddTitle(titles, mainWindowTitle, isFocused: false, "process");

        if (OperatingSystem.IsWindows())
            WindowsWindowTitleProbe.AddTitles(titles, processId);

        if (OperatingSystem.IsLinux())
        {
            var environment = LinuxProcessEnvironment.Read(processId);
            X11WindowTitleProbe.AddTitles(titles, processId, environment.GetValue("DISPLAY"));
            WaylandWindowTitleProbe.AddTitles(titles, processId, environment);
        }

        titles.Sort(static (left, right) => right.IsFocused.CompareTo(left.IsFocused));
        return titles;
    }

    internal static string? TryReadMainWindowTitle(int processId)
    {
        using var process = ProcessInspection.TryGetProcess(processId);
        if (process == null)
            return null;

        try
        {
            process.Refresh();
            var title = process.MainWindowTitle;
            return string.IsNullOrWhiteSpace(title)
                ? null
                : title.Trim();
        }
        catch
        {
            return null;
        }
    }

    internal static void AddTitle(
        List<UnityWindowTitleSignal> titles,
        string? title,
        bool isFocused,
        string source)
    {
        var normalizedTitle = title?.Trim();
        if (normalizedTitle is not { Length: > 0 })
            return;

        for (var index = 0; index < titles.Count; index++)
        {
            var existing = titles[index];
            if (!string.Equals(existing.Title, normalizedTitle, StringComparison.Ordinal))
                continue;

            if (isFocused && !existing.IsFocused)
                titles[index] = existing with { IsFocused = true, Source = source };
            return;
        }

        titles.Add(new(normalizedTitle, isFocused, source));
    }
}
