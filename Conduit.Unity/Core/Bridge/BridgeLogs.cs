#nullable enable

using System;
using UnityEngine;

namespace Conduit
{
    // one subscription routes command logs to their existing captures and idle logs to a lossy summary
    static class BridgeLogs
    {
        static readonly object gate = new();
        static Action<string, string, LogType>? captures;
        static BackgroundLogSummary? background;
        static string logPath = string.Empty;
        static bool backgroundEnabled;
        static bool hooked;

        internal static void Configure(bool enabled, string path)
        {
            lock (gate)
            {
                backgroundEnabled = enabled;
                logPath = path;
                if (!enabled)
                    background = null;
                UpdateSubscription();
            }
        }

        internal static void StartCapture(Action<string, string, LogType> capture)
        {
            lock (gate)
            {
                captures += capture;
                UpdateSubscription();
            }
        }

        internal static void StopCapture(Action<string, string, LogType> capture)
        {
            lock (gate)
            {
                captures -= capture;
                UpdateSubscription();
            }
        }

        internal static string? TakeBackground()
        {
            BackgroundLogSummary? summary;
            string path;
            lock (gate)
            {
                summary = background;
                background = null;
                path = logPath;
            }

            return summary?.Format(path); // stack cleanup and rendering never block the log callback
        }

        static void UpdateSubscription()
        {
            var needed = backgroundEnabled || captures != null;
            if (needed == hooked)
                return;

            if (needed)
                Application.logMessageReceivedThreaded += Record;
            else
                Application.logMessageReceivedThreaded -= Record;
            hooked = needed;
        }

        static void Record(string message, string stackTrace, LogType type)
        {
            lock (gate)
            {
                if (captures != null)
                    captures(message, stackTrace, type);
                else if (backgroundEnabled)
                    (background ??= new()).Record(message, stackTrace, type);
            }
        }
    }
}
