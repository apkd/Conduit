#nullable enable

using System;
using Process = System.Diagnostics.Process;

namespace Conduit
{
    /// <summary>Provides process facts shared by Editor and player status reports.</summary>
    static class BridgeStatusUtility
    {
        internal static readonly int ProcessId = GetProcessId();

        static int GetProcessId()
        {
            using var process = Process.GetCurrentProcess();
            return process.Id;
        }

        internal static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                duration = TimeSpan.Zero;

            string? primary = null;
            string? secondary = null;
            AddPart(duration.Days, "day");
            AddPart(duration.Hours, "hour");
            AddPart(duration.Minutes, "minute");
            AddPart(duration.Seconds, "second");
            if (primary == null)
                primary = "1 second";

            return secondary == null ? primary ?? "0 seconds" : primary + " " + secondary;

            void AddPart(int value, string unit)
            {
                if (value <= 0 || secondary != null)
                    return;

                var part = value == 1 ? $"1 {unit}" : $"{value} {unit}s";
                if (primary == null)
                    primary = part;
                else
                    secondary = part;
            }
        }
    }
}
