using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Conduit;

static class SafeModeWindowProbe
{
    const int WindowProbeTimeoutMilliseconds = 2000;

    internal static bool IsSafeModeWindowTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Contains("Safe Mode", StringComparison.OrdinalIgnoreCase);

    internal static string? TryReadSafeModeWindowSignal(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
            return null;

        return TryReadX11SafeModeWindowSignal(processId)
               ?? TryReadHyprlandSafeModeWindowSignal(processId);
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
        if (!element.TryGetProperty(propertyName, out var titleElement) || titleElement.ValueKind != JsonValueKind.String)
            return null;

        var title = titleElement.GetString();
        return IsSafeModeWindowTitle(title) ? title : null;
    }

    static string? TryReadHyprlandSafeModeWindowSignal(int processId)
    {
        try
        {
            using var process = Process.Start(CreateHyprlandClientsStartInfo(processId));
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
            if (process.ExitCode != 0)
                return null;

            return TryReadHyprlandClientsSafeModeWindowSignal(outputTask.GetAwaiter().GetResult(), processId);
        }
        catch
        {
            return null;
        }
    }

    static ProcessStartInfo CreateHyprlandClientsStartInfo(int processId)
    {
        var startInfo = new ProcessStartInfo("hyprctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("clients");
        startInfo.ArgumentList.Add("-j");
        CopyProcessEnvironmentValue(processId, startInfo, "HYPRLAND_INSTANCE_SIGNATURE");
        CopyProcessEnvironmentValue(processId, startInfo, "XDG_RUNTIME_DIR");
        CopyProcessEnvironmentValue(processId, startInfo, "WAYLAND_DISPLAY");
        return startInfo;
    }

    static void CopyProcessEnvironmentValue(int processId, ProcessStartInfo startInfo, string name)
    {
        if (TryReadProcessEnvironmentValue(processId, name) is { } value)
            startInfo.Environment[name] = value;
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
#else
    static string? TryReadX11SafeModeWindowSignal(int processId) => null;
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
