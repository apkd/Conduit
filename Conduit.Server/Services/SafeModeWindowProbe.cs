using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Conduit;

static class SafeModeWindowProbe
{
    const string HyprctlExecutableName = "hyprctl";
    const string NiriExecutableName = "niri";
    const string SwaymsgExecutableName = "swaymsg";
    const int WindowProbeTimeoutMilliseconds = 2000;
    static readonly string[] StandardExecutableDirectories =
    [
        "/run/current-system/sw/bin",
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
    ];

    internal static bool IsSafeModeWindowTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Contains("Safe Mode", StringComparison.OrdinalIgnoreCase);

    internal static string? TryReadSafeModeWindowSignal(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
            return null;

        return TryReadX11SafeModeWindowSignal(processId)
               ?? TryReadHyprlandSafeModeWindowSignal(processId)
               ?? TryReadSwaySafeModeWindowSignal(processId)
               ?? TryReadNiriSafeModeWindowSignal(processId);
    }

    internal static string? TryReadHyprlandClientsSafeModeWindowSignal(string? json, int processId)
    {
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var client in document.RootElement.EnumerateArray())
            {
                if (client.ValueKind != JsonValueKind.Object)
                    continue;

                if (!client.TryGetProperty("pid", out var pidElement)
                    || pidElement.ValueKind != JsonValueKind.Number
                    || !pidElement.TryGetInt32(out var clientProcessId)
                    || clientProcessId != processId)
                    continue;

                if (TryReadSafeModeTitle(client, "title") is { } title)
                    return title;

                if (TryReadSafeModeTitle(client, "initialTitle") is { } initialTitle)
                    return initialTitle;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadSafeModeTitle(JsonElement element, string propertyName)
    {
        var title = TryReadStringProperty(element, propertyName);
        return IsSafeModeWindowTitle(title) ? title : null;
    }

    static string? TryReadStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }

    static string? TryReadHyprlandSafeModeWindowSignal(int processId)
    {
        try
        {
            return RunWindowProbeCommand(CreateHyprlandClientsStartInfo(processId)) is { } output
                ? TryReadHyprlandClientsSafeModeWindowSignal(output, processId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryReadSwayTreeSafeModeWindowSignal(string? json, int processId)
    {
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryReadSwayNodeSafeModeWindowSignal(document.RootElement, processId);
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadSwayNodeSafeModeWindowSignal(JsonElement node, int processId)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return null;

        if (JsonElementPidMatches(node, processId))
        {
            if (TryReadSafeModeTitle(node, "name") is { } name)
                return name;

            if (node.TryGetProperty("window_properties", out var windowProperties)
                && windowProperties.ValueKind == JsonValueKind.Object
                && TryReadSafeModeTitle(windowProperties, "title") is { } title)
                return title;
        }

        if (TryReadSwayChildSafeModeWindowSignal(node, "nodes", processId) is { } nodeTitle)
            return nodeTitle;

        if (TryReadSwayChildSafeModeWindowSignal(node, "floating_nodes", processId) is { } floatingNodeTitle)
            return floatingNodeTitle;

        return null;
    }

    static string? TryReadSwayChildSafeModeWindowSignal(JsonElement node, string propertyName, int processId)
    {
        if (!node.TryGetProperty(propertyName, out var children) || children.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var child in children.EnumerateArray())
            if (TryReadSwayNodeSafeModeWindowSignal(child, processId) is { } title)
                return title;

        return null;
    }

    static bool JsonElementPidMatches(JsonElement element, int processId)
    {
        if (!element.TryGetProperty("pid", out var pidElement) || pidElement.ValueKind != JsonValueKind.Number)
            return false;

        return pidElement.TryGetInt32(out var nodeProcessId) && nodeProcessId == processId;
    }

    static string? TryReadSwaySafeModeWindowSignal(int processId)
    {
        try
        {
            return RunWindowProbeCommand(CreateSwayTreeStartInfo(processId)) is { } output
                ? TryReadSwayTreeSafeModeWindowSignal(output, processId)
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? TryReadNiriWindowsSafeModeWindowSignal(string? json, int processId)
    {
        if (string.IsNullOrWhiteSpace(json) || processId <= 0)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryReadNiriWindowsSafeModeWindowSignal(document.RootElement, processId);
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadNiriWindowsSafeModeWindowSignal(JsonElement root, int processId)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("Ok", out var ok)
            && ok.ValueKind == JsonValueKind.Object
            && ok.TryGetProperty("Windows", out var socketWindows))
            return TryReadNiriWindowsSafeModeWindowSignal(socketWindows, processId);

        if (root.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var window in root.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object || !JsonElementPidMatches(window, processId))
                continue;

            if (TryReadSafeModeTitle(window, "title") is { } title)
                return title;
        }

        return null;
    }

    static string? TryReadNiriSafeModeWindowSignal(int processId)
    {
        try
        {
            return RunWindowProbeCommand(CreateNiriWindowsStartInfo(processId)) is { } output
                ? TryReadNiriWindowsSafeModeWindowSignal(output, processId)
                : null;
        }
        catch
        {
            return null;
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
                HyprctlExecutableName,
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
                SwaymsgExecutableName,
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
                NiriExecutableName,
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

            return Directory
                .EnumerateFileSystemEntries(xdgRuntimeDirectory, "sway-ipc.*.sock")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
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

            return Directory
                .EnumerateFileSystemEntries(xdgRuntimeDirectory, pattern)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
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

            var bestCandidate = Directory
                .EnumerateDirectories(hyprlandDirectory)
                .Where(directory => File.Exists(Path.Combine(directory, ".socket.sock")))
                .Select(directory => new DirectoryInfo(directory))
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .FirstOrDefault();

            return bestCandidate?.Name;
        }
        catch
        {
            return null;
        }
    }

    static void TryKillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch { }
    }

#if CONDUIT_LINUX
    static string? TryReadX11SafeModeWindowSignal(int processId)
    {
        try
        {
            XSetErrorHandler(IgnoreX11Error);
            var display = XOpenDisplay(TryReadProcessEnvironmentValue(processId, "DISPLAY"));
            if (display == IntPtr.Zero)
                return null;

            try
            {
                return TryReadX11SafeModeWindowSignal(display, processId);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            return null;
        }
    }

    static string? TryReadX11SafeModeWindowSignal(IntPtr display, int processId)
    {
        var rootWindow = XDefaultRootWindow(display);
        var pidAtom = XInternAtom(display, "_NET_WM_PID", true);
        if (pidAtom == IntPtr.Zero)
            return null;

        foreach (var window in EnumerateX11ClientWindows(display, rootWindow))
        {
            if (ReadX11Cardinal(display, window, pidAtom) != processId)
                continue;

            foreach (var title in EnumerateX11WindowTitles(display, window))
                if (IsSafeModeWindowTitle(title))
                    return title;
        }

        return null;
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
    static string? TryReadX11SafeModeWindowSignal(int processId) => null;

    static uint getuid() => 0;
#endif

    static string? TryReadProcessEnvironmentValue(int processId, string name)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{processId}/environ");
            var prefix = Encoding.UTF8.GetBytes($"{name}=");
            var offset = 0;
            while (offset < bytes.Length)
            {
                var terminatorOffset = Array.IndexOf(bytes, (byte)0, offset);
                if (terminatorOffset < 0)
                    terminatorOffset = bytes.Length;

                var length = terminatorOffset - offset;
                if (length > prefix.Length
                    && bytes.AsSpan(offset, prefix.Length).SequenceEqual(prefix))
                    return Encoding.UTF8.GetString(bytes, offset + prefix.Length, length - prefix.Length);

                offset = terminatorOffset + 1;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
