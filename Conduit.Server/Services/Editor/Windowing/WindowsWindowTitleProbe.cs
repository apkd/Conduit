using System.Runtime.InteropServices;
using System.Text;

namespace Conduit;

static class WindowsWindowTitleProbe
{
#if CONDUIT_WINDOWS
    internal static void AddTitles(List<UnityWindowTitleSignal> titles, int processId)
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
                    UnityWindowTitleProbe.AddTitle(titles, title, window == foregroundWindow, "win32");

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
    internal static void AddTitles(List<UnityWindowTitleSignal> titles, int processId) { }
#endif
}
