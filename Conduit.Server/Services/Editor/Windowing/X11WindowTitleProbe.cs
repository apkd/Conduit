using System.Runtime.InteropServices;
using System.Text;

namespace Conduit;

static class X11WindowTitleProbe
{
#if CONDUIT_LINUX
    internal static void AddTitles(List<UnityWindowTitleSignal> titles, int processId, string? displayName)
    {
        try
        {
            XSetErrorHandler(IgnoreX11Error);
            var display = XOpenDisplay(displayName);
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
                UnityWindowTitleProbe.AddTitle(titles, title, window == activeWindow, "x11");
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

#else
    internal static void AddTitles(List<UnityWindowTitleSignal> titles, int processId, string? displayName) { }
#endif
}
