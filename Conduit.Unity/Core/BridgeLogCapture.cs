#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Conduit
{
    /// <summary>Captures and renders command logs consistently across Unity targets.</summary>
    sealed class BridgeLogCapture : IDisposable
    {
        readonly object gate = new();
        readonly Dictionary<LogSignature, int> indexes = new();
        readonly List<LogEntry> entries = new();
        bool hooked = true;

        public BridgeLogCapture()
            => Application.logMessageReceivedThreaded += OnLogMessageReceived;

        public string Drain()
        {
            Stop();
            lock (gate)
            {
                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    if (builder.Length > 0)
                        builder.AppendLine().AppendLine();

                    BridgeLogFormatter.Append(
                        builder,
                        entry.Message,
                        entry.StackTrace,
                        entry.RepeatCount
                    );
                }

                entries.Clear();
                indexes.Clear();
                return builder.ToString();
            }
        }

        public void Dispose() => Stop();

        void Stop()
        {
            lock (gate)
            {
                if (!hooked)
                    return;

                hooked = false;
            }
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
        }

        void OnLogMessageReceived(string condition, string stackTrace, LogType logType)
        {
            var stack = logType == LogType.Log
                ? null
                : BridgeExceptionFormatter.TrimCommonLogTail(
                    BridgeExceptionFormatter.SimplifyStackTrace(stackTrace)
                );
            var signature = new LogSignature(condition ?? string.Empty, stack ?? string.Empty, logType);
            lock (gate)
            {
                if (!hooked || signature.Message.Length == 0 && signature.StackTrace.Length == 0)
                    return;

                if (indexes.TryGetValue(signature, out var index))
                {
                    entries[index].RepeatCount++;
                    return;
                }

                indexes.Add(signature, entries.Count);
                entries.Add(new(signature.Message, signature.StackTrace));
            }
        }

        sealed class LogEntry
        {
            public LogEntry(string message, string stackTrace)
            {
                Message = message;
                StackTrace = stackTrace;
            }

            public string Message { get; }
            public string StackTrace { get; }
            public int RepeatCount { get; set; } = 1;
        }

        readonly struct LogSignature : IEquatable<LogSignature>
        {
            public LogSignature(string message, string stackTrace, LogType logType)
            {
                Message = message;
                StackTrace = stackTrace;
                LogType = logType;
            }

            public string Message { get; }
            public string StackTrace { get; }
            public LogType LogType { get; }

            public bool Equals(LogSignature other)
                => Message == other.Message
                   && StackTrace == other.StackTrace
                   && LogType == other.LogType;

            public override bool Equals(object? value)
                => value is LogSignature other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(Message);
                    hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(StackTrace);
                    return hash * 397 ^ (int)LogType;
                }
            }
        }
    }

    static class BridgeLogFormatter
    {
        public static void Append(
            StringBuilder builder,
            string message,
            string? stackTrace,
            int repeatCount = 1)
        {
            AppendQuotedLines(builder, message);
            if (!string.IsNullOrEmpty(stackTrace))
            {
                AppendSeparator(builder);
                builder.Append(stackTrace);
            }

            if (repeatCount <= 1)
                return;

            AppendSeparator(builder);
            builder.Append("*log repeated ")
                .Append(repeatCount)
                .Append(" times*");
        }

        static void AppendQuotedLines(StringBuilder builder, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            builder.Append("> ");
            for (var index = 0; index < message.Length; index++)
            {
                var character = message[index];
                if (character == '\r')
                    continue;

                builder.Append(character);
                if (character == '\n' && index + 1 < message.Length)
                    builder.Append("> ");
            }
        }

        static void AppendSeparator(StringBuilder builder)
        {
            if (builder.Length > 0)
                builder.AppendLine().AppendLine();
        }
    }
}
