using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Conduit;

internal readonly record struct UnityWindowTitleSignal(string Title, bool IsFocused, string Source);

static class UnityWindowTitleProbe
{
    const int WindowProbeTimeoutMilliseconds = 2000;
    static readonly string[] StandardExecutableDirectories =
    [
        "/run/current-system/sw/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
    ];

    [ThreadStatic] static bool processEnvironmentCacheActive;
    [ThreadStatic] static int processEnvironmentCacheProcessId;
    [ThreadStatic] static byte[]? processEnvironmentCache;
    [ThreadStatic] static Dictionary<string, string?>? processEnvironmentValueCache;

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

    internal static List<UnityWindowTitleSignal> ReadProcessWindowTitles(int processId)
        => ReadProcessWindowTitles(processId, TryReadMainWindowTitle(processId));

    static List<UnityWindowTitleSignal> ReadProcessWindowTitles(
        int processId,
        string? mainWindowTitle)
    {
        var titles = new List<UnityWindowTitleSignal>(4);
        if (processId <= 0)
            return titles;

        var previousCacheActive = processEnvironmentCacheActive;
        var previousCacheProcessId = processEnvironmentCacheProcessId;
        var previousCache = processEnvironmentCache;
        var previousValueCache = processEnvironmentValueCache;
        processEnvironmentCacheActive = true;
        processEnvironmentCacheProcessId = processId;
        processEnvironmentCache = TryReadProcessEnvironment(processId);
        processEnvironmentValueCache = new(StringComparer.Ordinal);
        try
        {
            // keep the cheap process api first; platform probes fill in secondary modal/progress windows.
            if (mainWindowTitle is not null)
                AddTitle(titles, mainWindowTitle, isFocused: false, "process");

            if (OperatingSystem.IsWindows())
                TryAddWindowsWindowTitles(titles, processId);

            if (OperatingSystem.IsLinux())
            {
                TryAddX11WindowTitles(titles, processId);
                AddTitles(titles, TryReadHyprlandWindowTitles(processId));
                AddTitles(titles, TryReadSwayWindowTitles(processId));
                AddTitles(titles, TryReadNiriWindowTitles(processId));
            }

            titles.Sort(static (left, right) => right.IsFocused.CompareTo(left.IsFocused));
            return titles;
        }
        finally
        {
            processEnvironmentCacheActive = previousCacheActive;
            processEnvironmentCacheProcessId = previousCacheProcessId;
            processEnvironmentCache = previousCache;
            processEnvironmentValueCache = previousValueCache;
        }
    }

    internal static string? TryReadMainWindowTitle(int processId)
    {
        using var process = ConduitUtility.TryGetProcess(processId);
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

    internal static IReadOnlyList<UnityWindowTitleSignal> ReadHyprlandClientsWindowTitles(string? json, int processId)
    {
        var titles = new List<UnityWindowTitleSignal>(2);
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return titles;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return titles;

            foreach (var client in document.RootElement.EnumerateArray())
            {
                if (client.ValueKind != JsonValueKind.Object || !JsonElementPidMatches(client, processId))
                    continue;

                var isFocused = TryReadIntProperty(client, "focusHistoryID") == 0;
                AddJsonStringTitle(titles, client, "title", isFocused, "hyprland");
                AddJsonStringTitle(titles, client, "initialTitle", isFocused, "hyprland");
            }
        }
        catch { }

        return titles;
    }

    internal static IReadOnlyList<UnityWindowTitleSignal> ReadSwayTreeWindowTitles(string? json, int processId)
    {
        var titles = new List<UnityWindowTitleSignal>(2);
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return titles;

        try
        {
            using var document = JsonDocument.Parse(json);
            AddSwayNodeWindowTitles(titles, document.RootElement, processId);
        }
        catch { }

        return titles;
    }

    internal static IReadOnlyList<UnityWindowTitleSignal> ReadNiriWindowsWindowTitles(string? json, int processId)
    {
        var titles = new List<UnityWindowTitleSignal>(2);
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return titles;

        try
        {
            using var document = JsonDocument.Parse(json);
            AddNiriWindowTitles(titles, document.RootElement, processId);
        }
        catch { }

        return titles;
    }

    static void AddSwayNodeWindowTitles(List<UnityWindowTitleSignal> titles, JsonElement node, int processId)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (JsonElementPidMatches(node, processId))
        {
            var isFocused = TryReadBoolProperty(node, "focused");
            AddJsonStringTitle(titles, node, "name", isFocused, "sway");
            if (node.TryGetProperty("window_properties", out var windowProperties)
                && windowProperties.ValueKind == JsonValueKind.Object)
                AddJsonStringTitle(titles, windowProperties, "title", isFocused, "sway");
        }

        AddSwayChildWindowTitles(titles, node, "nodes", processId);
        AddSwayChildWindowTitles(titles, node, "floating_nodes", processId);
    }

    static void AddSwayChildWindowTitles(
        List<UnityWindowTitleSignal> titles,
        JsonElement node,
        string propertyName,
        int processId
    )
    {
        if (!node.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return;

        foreach (var child in children.EnumerateArray())
            AddSwayNodeWindowTitles(titles, child, processId);
    }

    static void AddNiriWindowTitles(List<UnityWindowTitleSignal> titles, JsonElement root, int processId)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Ok", out var ok)
            && ok.ValueKind == JsonValueKind.Object
            && ok.TryGetProperty("Windows", out var socketWindows))
        {
            AddNiriWindowTitles(titles, socketWindows, processId);
            return;
        }

        if (root.ValueKind != JsonValueKind.Array)
            return;

        foreach (var window in root.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || !JsonElementPidMatches(window, processId))
                continue;

            AddJsonStringTitle(titles, window, "title", TryReadBoolProperty(window, "is_focused"), "niri");
        }
    }

    static bool JsonElementPidMatches(JsonElement element, int processId)
    {
        if (!element.TryGetProperty("pid", out var pidElement) || pidElement.ValueKind != JsonValueKind.Number)
            return false;

        return pidElement.TryGetInt32(out var nodeProcessId) && nodeProcessId == processId;
    }

    static void AddJsonStringTitle(
        List<UnityWindowTitleSignal> titles,
        JsonElement element,
        string propertyName,
        bool isFocused,
        string source
    )
    {
        if (TryReadStringProperty(element, propertyName) is { } title)
            AddTitle(titles, title, isFocused, source);
    }

    static string? TryReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }

    static int? TryReadIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            return null;

        return property.TryGetInt32(out var value) ? value : null;
    }

    static bool TryReadBoolProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.True;
    }

    static void AddTitles(List<UnityWindowTitleSignal> destination, IEnumerable<UnityWindowTitleSignal> source)
    {
        foreach (var signal in source)
            AddTitle(destination, signal.Title, signal.IsFocused, signal.Source);
    }

    static void AddTitle(List<UnityWindowTitleSignal> titles, string? title, bool isFocused, string source)
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

    static IReadOnlyList<UnityWindowTitleSignal> TryReadHyprlandWindowTitles(int processId)
    {
        try
        {
            return RunWindowProbeCommand(CreateHyprlandClientsStartInfo(processId)) is { } output
                ? ReadHyprlandClientsWindowTitles(output, processId)
                : [];
        }
        catch
        {
            return [];
        }
    }

    static IReadOnlyList<UnityWindowTitleSignal> TryReadSwayWindowTitles(int processId)
    {
        try
        {
            return RunWindowProbeCommand(CreateSwayTreeStartInfo(processId)) is { } output
                ? ReadSwayTreeWindowTitles(output, processId)
                : [];
        }
        catch
        {
            return [];
        }
    }

    static IReadOnlyList<UnityWindowTitleSignal> TryReadNiriWindowTitles(int processId)
    {
        try
        {
            return RunWindowProbeCommand(CreateNiriWindowsStartInfo(processId)) is { } output
                ? ReadNiriWindowsWindowTitles(output, processId)
                : [];
        }
        catch
        {
            return [];
        }
    }

    static string? RunWindowProbeCommand(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process == null)
            return null;

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(WindowProbeTimeoutMilliseconds))
        {
            TryKillProcessTree(process);
            return null;
        }

        _ = errorTask.GetAwaiter().GetResult();
        return process.ExitCode == 0 ? outputTask.GetAwaiter().GetResult() : null;
    }

    static ProcessStartInfo CreateHyprlandClientsStartInfo(int processId)
    {
        var processPath = TryReadProcessEnvironmentValue(processId, "PATH");
        var startInfo = new ProcessStartInfo(
            ResolveExecutablePath(
                "hyprctl",
                processPath,
                Environment.GetEnvironmentVariable("PATH")
            )
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("clients");
        startInfo.ArgumentList.Add("-j");
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var xdgRuntimeDirectory = ResolveXdgRuntimeDirectory(TryReadProcessEnvironmentValue(processId, "XDG_RUNTIME_DIR"));
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            startInfo.Environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;

        var waylandDisplay = TryReadProcessEnvironmentValue(processId, "WAYLAND_DISPLAY")
                             ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
            startInfo.Environment["WAYLAND_DISPLAY"] = waylandDisplay;

        var signature = TryReadProcessEnvironmentValue(processId, "HYPRLAND_INSTANCE_SIGNATURE")
                        ?? Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (!string.IsNullOrWhiteSpace(signature))
            startInfo.Environment["HYPRLAND_INSTANCE_SIGNATURE"] = signature;
        else if (TryInferHyprlandInstanceSignature(xdgRuntimeDirectory) is { } inferredSignature)
            startInfo.Environment["HYPRLAND_INSTANCE_SIGNATURE"] = inferredSignature;

        return startInfo;
    }

    static ProcessStartInfo CreateSwayTreeStartInfo(int processId)
    {
        var processPath = TryReadProcessEnvironmentValue(processId, "PATH");
        var startInfo = new ProcessStartInfo(
            ResolveExecutablePath(
                "swaymsg",
                processPath,
                Environment.GetEnvironmentVariable("PATH")
            )
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var xdgRuntimeDirectory = ResolveXdgRuntimeDirectory(TryReadProcessEnvironmentValue(processId, "XDG_RUNTIME_DIR"));
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            startInfo.Environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;

        if (TryFindSwaySocket(
                xdgRuntimeDirectory,
                TryReadProcessEnvironmentValue(processId, "SWAYSOCK"),
                TryReadProcessEnvironmentValue(processId, "I3SOCK"),
                Environment.GetEnvironmentVariable("SWAYSOCK"),
                Environment.GetEnvironmentVariable("I3SOCK")
            )
            is { } socketPath)
        {
            startInfo.ArgumentList.Add("--socket");
            startInfo.ArgumentList.Add(socketPath);
        }

        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("get_tree");
        startInfo.ArgumentList.Add("--raw");
        return startInfo;
    }

    static ProcessStartInfo CreateNiriWindowsStartInfo(int processId)
    {
        var processPath = TryReadProcessEnvironmentValue(processId, "PATH");
        var startInfo = new ProcessStartInfo(
            ResolveExecutablePath(
                "niri",
                processPath,
                Environment.GetEnvironmentVariable("PATH")
            )
        )
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(processPath))
            startInfo.Environment["PATH"] = processPath;

        var xdgRuntimeDirectory = ResolveXdgRuntimeDirectory(TryReadProcessEnvironmentValue(processId, "XDG_RUNTIME_DIR"));
        if (!string.IsNullOrWhiteSpace(xdgRuntimeDirectory))
            startInfo.Environment["XDG_RUNTIME_DIR"] = xdgRuntimeDirectory;

        var waylandDisplay = TryReadProcessEnvironmentValue(processId, "WAYLAND_DISPLAY")
                             ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        if (!string.IsNullOrWhiteSpace(waylandDisplay))
            startInfo.Environment["WAYLAND_DISPLAY"] = waylandDisplay;

        if (TryFindNiriSocket(
                xdgRuntimeDirectory,
                waylandDisplay,
                TryReadProcessEnvironmentValue(processId, "NIRI_SOCKET"),
                Environment.GetEnvironmentVariable("NIRI_SOCKET")
            )
            is { } socketPath)
            startInfo.Environment["NIRI_SOCKET"] = socketPath;

        startInfo.ArgumentList.Add("msg");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("windows");
        return startInfo;
    }

    static string? ResolveXdgRuntimeDirectory(string? preferredDirectory)
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

    static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch { }
    }

#if CONDUIT_WINDOWS
    static void TryAddWindowsWindowTitles(List<UnityWindowTitleSignal> titles, int processId)
    {
        try
        {
            var foregroundWindow = GetForegroundWindow();
            _ = EnumWindows((window, lParam) =>
            {
                if (!IsWindowVisible(window))
                    return true;

                _ = GetWindowThreadProcessId(window, out var windowProcessId);
                if (windowProcessId != (uint)processId)
                    return true;

                if (TryReadWindowsWindowTitle(window) is { } title)
                    AddTitle(titles, title, window == foregroundWindow, "win32");

                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    static string? TryReadWindowsWindowTitle(IntPtr window)
    {
        var length = GetWindowTextLengthW(window);
        if (length <= 0)
            return null;

        var builder = new StringBuilder(length + 1);
        return GetWindowTextW(window, builder, builder.Capacity) > 0
            ? builder.ToString()
            : null;
    }

    delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
#else
    static void TryAddWindowsWindowTitles(List<UnityWindowTitleSignal> titles, int processId) { }
#endif

#if CONDUIT_LINUX
    static void TryAddX11WindowTitles(List<UnityWindowTitleSignal> titles, int processId)
    {
        try
        {
            XSetErrorHandler(IgnoreX11Error);
            var display = XOpenDisplay(TryReadProcessEnvironmentValue(processId, "DISPLAY"));
            if (display == IntPtr.Zero)
                return;

            try
            {
                AddX11WindowTitles(titles, display, processId);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch { }
    }

    static void AddX11WindowTitles(List<UnityWindowTitleSignal> titles, IntPtr display, int processId)
    {
        var rootWindow = XDefaultRootWindow(display);
        var pidAtom = XInternAtom(display, "_NET_WM_PID", true);
        if (pidAtom == IntPtr.Zero)
            return;

        var activeWindowAtom = XInternAtom(display, "_NET_ACTIVE_WINDOW", true);
        var activeWindow = ReadX11Window(display, rootWindow, activeWindowAtom);
        foreach (var window in EnumerateX11ClientWindows(display, rootWindow))
        {
            if (ReadX11Cardinal(display, window, pidAtom) != processId)
                continue;

            foreach (var title in EnumerateX11WindowTitles(display, window))
                AddTitle(titles, title, window == activeWindow, "x11");
        }
    }

    static IntPtr[] EnumerateX11ClientWindows(IntPtr display, IntPtr rootWindow)
    {
        var stackingAtom = XInternAtom(display, "_NET_CLIENT_LIST_STACKING", true);
        if (ReadX11WindowArray(display, rootWindow, stackingAtom) is { Length: > 0 } stackingWindows)
            return stackingWindows;

        var clientListAtom = XInternAtom(display, "_NET_CLIENT_LIST", true);
        return ReadX11WindowArray(display, rootWindow, clientListAtom);
    }

    static IEnumerable<string> EnumerateX11WindowTitles(IntPtr display, IntPtr window)
    {
        var visibleNameAtom = XInternAtom(display, "_NET_WM_VISIBLE_NAME", true);
        var nameAtom = XInternAtom(display, "_NET_WM_NAME", true);
        var utf8Atom = XInternAtom(display, "UTF8_STRING", true);
        var wmNameAtom = XInternAtom(display, "WM_NAME", true);

        if (ReadX11Text(display, window, visibleNameAtom, utf8Atom) is { } visibleName)
            yield return visibleName;

        if (ReadX11Text(display, window, nameAtom, utf8Atom) is { } name)
            yield return name;

        if (ReadX11Text(display, window, wmNameAtom, IntPtr.Zero) is { } wmName)
            yield return wmName;
    }

    static IntPtr ReadX11Window(IntPtr display, IntPtr window, IntPtr property)
        => ReadX11WindowArray(display, window, property).FirstOrDefault();

    static IntPtr[] ReadX11WindowArray(IntPtr display, IntPtr window, IntPtr property)
    {
        if (property == IntPtr.Zero)
            return [];

        var status = XGetWindowProperty(
            display,
            window,
            property,
            IntPtr.Zero,
            new(4096),
            false,
            IntPtr.Zero,
            out _,
            out var format,
            out var count,
            out _,
            out var data
        );
        if (status != 0 || data == IntPtr.Zero)
            return [];

        try
        {
            if (format != 32 || count == IntPtr.Zero)
                return [];

            var windowCount = checked((int)count.ToInt64());
            var windows = new IntPtr[windowCount];
            for (var index = 0; index < windowCount; index++)
                windows[index] = Marshal.ReadIntPtr(data, index * IntPtr.Size);

            return windows;
        }
        finally
        {
            XFree(data);
        }
    }

    static int? ReadX11Cardinal(IntPtr display, IntPtr window, IntPtr property)
    {
        if (property == IntPtr.Zero)
            return null;

        var status = XGetWindowProperty(
            display,
            window,
            property,
            IntPtr.Zero,
            new(1),
            false,
            IntPtr.Zero,
            out _,
            out var format,
            out var count,
            out _,
            out var data
        );
        if (status != 0 || data == IntPtr.Zero)
            return null;

        try
        {
            if (format != 32 || count == IntPtr.Zero)
                return null;

            return IntPtr.Size == 8
                ? checked((int)Marshal.ReadInt64(data))
                : Marshal.ReadInt32(data);
        }
        finally
        {
            XFree(data);
        }
    }

    static string? ReadX11Text(IntPtr display, IntPtr window, IntPtr property, IntPtr expectedType)
    {
        if (property == IntPtr.Zero)
            return null;

        var status = XGetWindowProperty(
            display,
            window,
            property,
            IntPtr.Zero,
            new(1024),
            false,
            expectedType,
            out _,
            out var format,
            out var count,
            out _,
            out var data
        );
        if (status != 0 || data == IntPtr.Zero)
            return null;

        try
        {
            if (format != 8 || count == IntPtr.Zero)
                return null;

            var byteCount = checked((int)count.ToInt64());
            if (byteCount <= 0)
                return null;

            var bytes = new byte[byteCount];
            Marshal.Copy(data, bytes, 0, byteCount);
            var text = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            XFree(data);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    static readonly XErrorHandler IgnoreX11Error = (_, _) => 0;

    [DllImport("libX11.so.6")]
    static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    static extern IntPtr XSetErrorHandler(XErrorHandler handler);

    [DllImport("libX11.so.6")]
    static extern int XGetWindowProperty(
        IntPtr display,
        IntPtr window,
        IntPtr property,
        IntPtr longOffset,
        IntPtr longLength,
        bool delete,
        IntPtr reqType,
        out IntPtr actualType,
        out int actualFormat,
        out IntPtr nitems,
        out IntPtr bytesAfter,
        out IntPtr prop
    );

    [DllImport("libc")]
    static extern uint getuid();
#else
    static void TryAddX11WindowTitles(List<UnityWindowTitleSignal> titles, int processId) { }

    static uint getuid() => 0;
#endif

    internal static string? TryReadProcessEnvironmentValue(int processId, string name)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var useCache = processEnvironmentCacheActive
                           && processEnvironmentCacheProcessId == processId;
            if (useCache
                && processEnvironmentValueCache!.TryGetValue(name, out var cachedValue))
                return cachedValue;

            var bytes = useCache
                ? processEnvironmentCache
                : TryReadProcessEnvironment(processId);
            if (bytes is null)
            {
                if (useCache)
                    processEnvironmentValueCache![name] = null;
                return null;
            }

            var nameByteCount = Encoding.UTF8.GetByteCount(name);
            Span<byte> encodedName = nameByteCount <= 128
                ? stackalloc byte[nameByteCount]
                : new byte[nameByteCount];
            Encoding.UTF8.GetBytes(name, encodedName);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var terminatorOffset = Array.IndexOf(bytes, (byte)0, offset);
                if (terminatorOffset < 0)
                    terminatorOffset = bytes.Length;

                var length = terminatorOffset - offset;
                if (length > nameByteCount
                    && bytes[offset + nameByteCount] == (byte)'='
                    && bytes.AsSpan(offset, nameByteCount).SequenceEqual(encodedName))
                {
                    var value = Encoding.UTF8.GetString(
                        bytes,
                        offset + nameByteCount + 1,
                        length - nameByteCount - 1
                    );
                    if (useCache)
                        processEnvironmentValueCache![name] = value;
                    return value;
                }

                offset = terminatorOffset + 1;
            }

            if (useCache)
                processEnvironmentValueCache![name] = null;
            return null;
        }
        catch
        {
            return null;
        }
    }

    static byte[]? TryReadProcessEnvironment(int processId)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            return File.ReadAllBytes($"/proc/{processId}/environ");
        }
        catch
        {
            return null;
        }
    }
}
