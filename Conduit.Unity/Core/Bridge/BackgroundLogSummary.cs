#nullable enable

using System;
using System.Text;
using UnityEngine;

namespace Conduit
{
    // retain early causes, not the latest tail of an error cascade; bound collection work as well as storage
    sealed class BackgroundLogSummary
    {
        internal const int MaxGroups = 6;
        internal const int InspectionLimit = 64;
        internal const int MaxOutputLength = 2000;
        const int MaxMessageLength = 256;
        const int MaxInspectedLength = 1024;
        const int MaxStackLength = 1024;
        const int QuietGroupLimit = 3;
        readonly Group[] groups = new Group[MaxGroups];
        readonly int[] inspected = new int[3];
        readonly long[] omitted = new long[3];
        int count;
        long order;
        bool trimmed;
        bool busy;

        internal void Record(string message, string stackTrace, LogType type)
        {
            var severity = type == LogType.Log ? 0 : type == LogType.Warning ? 1 : 2;
            if (inspected[severity] == InspectionLimit)
            {
                omitted[severity]++;
                trimmed = true;
                MakeBusy();
                return;
            }
            inspected[severity]++;

            var text = message.AsSpan(0, Math.Min(message.Length, MaxInspectedLength));
            var fingerprint = LogMessageFingerprint.Compute(text);
            for (var index = 0; index < count; index++)
            {
                ref var group = ref groups[index];
                if (group.Severity != severity || group.Fingerprint != fingerprint)
                    continue;

                group.Count++;
                if (!message.AsSpan().SequenceEqual(group.Message.AsSpan()))
                    trimmed = true;
                return;
            }

            var slot = count;
            if (count == groups.Length)
            {
                slot = -1;
                for (var index = 0; index < count; index++)
                    if (groups[index].Severity < severity
                        && (slot < 0 || groups[index].Severity < groups[slot].Severity
                            || groups[index].Severity == groups[slot].Severity && groups[index].Order > groups[slot].Order))
                        slot = index;

                trimmed = true;
                if (slot < 0)
                {
                    omitted[severity]++;
                    return;
                }
                omitted[groups[slot].Severity] += groups[slot].Count;
            }
            else
            {
                if (count >= QuietGroupLimit)
                    MakeBusy();
                count++;
            }
            trimmed |= message.Length > MaxMessageLength;
            groups[slot] = new()
            {
                Fingerprint = fingerprint,
                Severity = severity,
                Order = order++,
                Count = 1,
                Message = Clip(message, MaxMessageLength),
                Stack = severity > 0 && !busy ? Clip(stackTrace, MaxStackLength) : string.Empty,
            };
            trimmed |= severity > 0 && !busy && stackTrace.Length > MaxStackLength;
        }

        internal string Format(string logPath)
        {
            Array.Sort(groups, 0, count, GroupComparer.Instance);
            using var pooled = BridgeStringBuilderPool.Rent(out var builder);
            foreach (var group in groups.AsSpan(0, count))
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(group.Severity == 2 ? "error: " : group.Severity == 1 ? "warning: " : "info: ");
                AppendSingleLine(builder, group.Message);
                if (group.Count > 1)
                    builder.Append(" (×").Append(group.Count).Append(" similar)");

                if (!busy && group.Stack.Length > 0)
                    trimmed |= AppendStack(builder, group.Stack);
            }

            var footer = BuildFooter(logPath);
            // reserve the fallback path before trimming rendered content
            var remaining = Math.Max(0, MaxOutputLength - footer.Length - 1);
            if (builder.Length > remaining)
            {
                trimmed = true;
                footer = BuildFooter(logPath);
                remaining = Math.Max(0, MaxOutputLength - footer.Length - 1);
                builder.Length = Math.Min(builder.Length, remaining);
                if (builder.Length > 0)
                    builder[builder.Length - 1] = '…';
            }
            if (footer.Length > 0)
                builder.Append('\n').Append(footer);
            return builder.ToString();
        }

        void MakeBusy()
        {
            if (busy)
                return;
            busy = true;
            for (var index = 0; index < count; index++)
            {
                trimmed |= groups[index].Stack.Length > 0;
                groups[index].Stack = string.Empty;
            }
        }

        string BuildFooter(string logPath)
        {
            if (!trimmed)
                return string.Empty;

            var dropped = omitted[0] + omitted[1] + omitted[2];
            var notice = dropped > 0 ? $"Condensed; {dropped} more events omitted. " : "Condensed; details omitted. ";
            var path = string.IsNullOrWhiteSpace(logPath) ? "Unity log path unavailable." : "Full logs: " + logPath;
            if (notice.Length + path.Length >= MaxOutputLength)
                path = "Unity log path exceeds the summary limit; use status to retrieve it.";
            return notice + path;
        }

        static string Clip(string value, int limit)
            => value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";

        static void AppendSingleLine(StringBuilder builder, string value)
        {
            foreach (var character in value)
                builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
        }

        static bool AppendStack(StringBuilder builder, string stack)
        {
            var simplified = BridgeExceptionFormatter.TrimCommonLogTail(BridgeExceptionFormatter.SimplifyStackTrace(stack));
            var remaining = simplified.AsSpan();
            var frames = 0;
            var trimmed = false;
            while (!remaining.IsEmpty && frames < 2)
            {
                var newline = remaining.IndexOf('\n');
                var frame = (newline < 0 ? remaining : remaining.Slice(0, newline)).Trim();
                remaining = newline < 0 ? default : remaining.Slice(newline + 1);
                if (frame.IsEmpty || frame.StartsWith("UnityEngine.Debug".AsSpan(), StringComparison.Ordinal)
                    || frame.StartsWith("UnityEngine.Logger".AsSpan(), StringComparison.Ordinal))
                    continue;

                builder.Append("\n  ").Append(frame.Slice(0, Math.Min(frame.Length, 128)));
                trimmed |= frame.Length > 128;
                frames++;
            }
            return trimmed || !remaining.Trim().IsEmpty;
        }

        struct Group
        {
            internal ulong Fingerprint;
            internal int Severity;
            internal long Order;
            internal long Count;
            internal string Message;
            internal string Stack;
        }

        sealed class GroupComparer : System.Collections.Generic.IComparer<Group>
        {
            internal static readonly GroupComparer Instance = new();
            public int Compare(Group left, Group right)
            {
                var severity = right.Severity.CompareTo(left.Severity);
                return severity != 0 ? severity : left.Order.CompareTo(right.Order);
            }
        }
    }
}
