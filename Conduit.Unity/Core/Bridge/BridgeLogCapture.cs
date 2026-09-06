#nullable enable

using System;
using UnityEngine;

namespace Conduit
{
    /// <summary>Captures player command logs through the shared Unity log source.</summary>
    sealed class BridgeLogCapture : IDisposable
    {
        readonly object gate = new();
        CapturedLogEntries? entries;
        string? lastMessage;
        string? lastStack;
        LogType lastType;
        int lastIndex = -1;
        bool hooked = true;

        public BridgeLogCapture() => BridgeLogs.StartCapture(Record);

        public string Drain()
        {
            Stop();
            lock (gate)
            {
                if (entries == null)
                    return string.Empty;

                using var pooled = BridgeStringBuilderPool.Rent(out var builder);
                foreach (var entry in entries.Entries)
                {
                    if (builder.Length > 0)
                        builder.Append("\n\n");
                    BridgeLogFormatter.Append(builder, entry.Message, entry.StackTrace, entry.RepeatCount);
                }
                Release();
                return builder.ToString();
            }
        }

        public void Dispose()
        {
            Stop();
            lock (gate)
                Release();
        }

        void Stop()
        {
            if (!hooked)
                return;
            BridgeLogs.StopCapture(Record);
            hooked = false;
        }

        void Record(string message, string stack, LogType type)
        {
            lock (gate)
            {
                if (lastIndex >= 0 && type == lastType && message == lastMessage && stack == lastStack)
                {
                    entries!.Entries[lastIndex].RepeatCount++;
                    return;
                }

                var cleaned = type == LogType.Log
                    ? string.Empty
                    : BridgeExceptionFormatter.TrimCommonLogTail(BridgeExceptionFormatter.SimplifyStackTrace(stack)) ?? string.Empty;
                lastIndex = (entries ??= CapturedLogEntries.Rent()).Add(message, cleaned, type);
                lastMessage = message;
                lastStack = stack;
                lastType = type;
            }
        }

        void Release()
        {
            entries?.Release();
            entries = null;
            lastMessage = null;
            lastStack = null;
            lastIndex = -1;
        }
    }
}
