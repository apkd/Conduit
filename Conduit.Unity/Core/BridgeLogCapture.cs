#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Pool;

namespace Conduit
{
    /// <summary>Captures and renders command logs consistently across Unity targets.</summary>
    sealed class BridgeLogCapture : IDisposable
    {
        readonly object gate = new();
        Dictionary<LogSignature, int>? indexes;
        List<LogEntry>? entries;
        string? lastRawMessage;
        string? lastRawStackTrace;
        int lastRawEntryIndex;
        LogType lastRawLogType;
        bool hasLastRawEntry;
        bool hooked = true;

        public BridgeLogCapture()
            => Application.logMessageReceivedThreaded += OnLogMessageReceived;

        public string Drain()
        {
            Stop();
            lock (gate)
            {
                if (entries is not { Count: > 0 })
                {
                    ReleaseCollections();
                    return string.Empty;
                }

                using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
                foreach (var entry in entries)
                {
                    if (builder.Length > 0)
                        builder.Append("\n\n");

                    BridgeLogFormatter.Append(
                        builder,
                        entry.Message,
                        entry.StackTrace,
                        entry.RepeatCount
                    );
                }

                var result = builder.ToString();
                ReleaseCollections();
                return result;
            }
        }

        public void Dispose()
        {
            Stop();
            lock (gate)
                ReleaseCollections();
        }

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
            var rawMessage = condition ?? string.Empty;
            var rawStackTrace = stackTrace ?? string.Empty;
            lock (gate)
            {
                // repeated logs bypass stack cleanup, which dominates log-storm capture cost.
                if (!hooked)
                    return;
                if (hasLastRawEntry
                    && logType == lastRawLogType
                    && rawMessage == lastRawMessage
                    && rawStackTrace == lastRawStackTrace)
                {
                    if (lastRawEntryIndex >= 0)
                        entries![lastRawEntryIndex].RepeatCount++;
                    return;
                }
            }

            var stack = logType == LogType.Log
                ? null
                : BridgeExceptionFormatter.TrimCommonLogTail(
                    BridgeExceptionFormatter.SimplifyStackTrace(stackTrace)
                );
            var signature = new LogSignature(rawMessage, stack ?? string.Empty, logType);
            lock (gate)
            {
                if (!hooked)
                    return;

                int entryIndex;
                if (signature.Message.Length == 0 && signature.StackTrace.Length == 0)
                    entryIndex = -1;
                else if (indexes != null && indexes.TryGetValue(signature, out entryIndex))
                    entries![entryIndex].RepeatCount++;
                else
                {
                    if (indexes == null)
                    {
                        _ = DictionaryPool<LogSignature, int>.Get(out indexes);
                        indexes.Clear();
                    }
                    if (entries == null)
                    {
                        _ = ListPool<LogEntry>.Get(out entries);
                        entries.Clear();
                    }
                    entryIndex = entries.Count;
                    indexes.Add(signature, entryIndex);
                    entries.Add(new(signature.Message, signature.StackTrace));
                }

                lastRawMessage = rawMessage;
                lastRawStackTrace = rawStackTrace;
                lastRawLogType = logType;
                lastRawEntryIndex = entryIndex;
                hasLastRawEntry = true;
            }
        }

        void ReleaseCollections()
        {
            if (entries != null)
            {
                ListPool<LogEntry>.Release(entries);
                entries = null;
            }
            if (indexes != null)
            {
                DictionaryPool<LogSignature, int>.Release(indexes);
                indexes = null;
            }

            lastRawMessage = null;
            lastRawStackTrace = null;
            hasLastRawEntry = false;
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
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(message);
                    hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(stackTrace);
                    HashCode = hash * 397 ^ (int)logType;
                }
            }

            public string Message { get; }
            public string StackTrace { get; }
            public LogType LogType { get; }
            int HashCode { get; }

            public bool Equals(LogSignature other)
                => Message == other.Message
                   && StackTrace == other.StackTrace
                   && LogType == other.LogType;

            public override bool Equals(object? value)
                => value is LogSignature other && Equals(other);

            public override int GetHashCode() => HashCode;
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
                builder.Append("\n\n");
        }
    }
}
