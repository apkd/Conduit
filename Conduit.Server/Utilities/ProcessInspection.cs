using System.Diagnostics;

namespace Conduit;

static class ProcessInspection
{
    /// <summary>
    /// Gets a live <see cref="Process"/> instance when the process still exists and is accessible.
    /// </summary>
    internal static Process? TryGetProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the executable path for a process when the platform allows it.
    /// </summary>
    internal static string? TryGetProcessPath(Process? process)
    {
        if (process == null)
            return null;

        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

}
