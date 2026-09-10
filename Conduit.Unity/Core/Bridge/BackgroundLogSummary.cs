#nullable enable

using System;
using System.Text;
using UnityEngine;

namespace Conduit
{
    // retain early causes, not the latest tail of an error cascade; bound collection work as well as storage
    sealed class BackgroundLogSummary
    {
        internal const int MaxGroups = 16;
        internal const int InspectionLimit = 64;
        internal const int MaxOutputLength = 2000;
        const int MaxMessageLength = 256;
        const int MaxInspectedLength = 1024;
        const int MaxStackLength = 1024;
        readonly Group[] groups = new Group[MaxGroups];
        readonly int[] inspected = new int[3];
        int count;
        long order;
        long omitted;

        internal void Record(string message, string stackTrace, LogType type)
        {
            var severity = type == LogType.Log ? 0 : type == LogType.Warning ? 1 : 2;
            if (inspected[severity] == InspectionLimit)
            {
                omitted++;
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

                if (slot < 0)
                {
                    omitted++;
                    return;
                }
                omitted += groups[slot].Count;
            }
            else
                count++;

            groups[slot] = new()
            {
                Fingerprint = fingerprint,
                Severity = severity,
                Order = order++,
                Count = 1,
                Message = Clip(message, MaxMessageLength),
                Stack = severity > 0 ? Clip(stackTrace, MaxStackLength) : string.Empty,
            };
        }

        internal string Format()
        {
            Array.Sort(groups, 0, count, GroupComparer.Instance);
            using var pooled = BridgeStringBuilderPool.Rent(out var builder);
            Span<int> starts = stackalloc int[count];
            Span<int> lengths = stackalloc int[count];
            var totalEvents = omitted;
            for (var index = 0; index < count; index++)
            {
                ref var group = ref groups[index];
                starts[index] = builder.Length;
                totalEvents += group.Count;
                builder.Append(group.Severity == 2 ? "> [ERROR] " : group.Severity == 1 ? "> [WARN] " : "> ");
                AppendSingleLine(builder, group.Message);
                if (group.Count > 1)
                    builder.Append(" (×").Append(group.Count).Append(" similar)");

                if (group.Stack.Length > 0)
                    AppendStack(builder, group.Stack);
                builder.Append('\n');
                lengths[index] = builder.Length - starts[index];
            }

            if (omitted == 0 && builder.Length <= MaxOutputLength + 1)
            {
                if (builder.Length > 0)
                    builder.Length--;
                return builder.ToString();
            }

            // reserve enough space for the omission count before choosing complete groups.
            var remaining = MaxOutputLength - FormatOmissions(totalEvents).Length;
            Span<bool> included = stackalloc bool[count];
            for (var severity = 2; severity >= 0; severity--)
            for (var index = 0; index < count; index++)
            {
                if (groups[index].Severity != severity)
                    continue;
                included[index] = lengths[index] <= remaining;
                if (included[index])
                    remaining -= lengths[index];
            }

            var dropped = omitted;
            // removing from the end preserves the recorded positions and chronological order.
            for (var index = count - 1; index >= 0; index--)
            {
                if (included[index])
                    continue;
                dropped += groups[index].Count;
                builder.Remove(starts[index], lengths[index]);
            }
            return builder.Append(FormatOmissions(dropped)).ToString();

            static string FormatOmissions(long events)
                => $"{events} more {(events == 1 ? "event" : "events")} omitted.";
        }

        static string Clip(string value, int limit)
            => value.Length <= limit ? value : value.Substring(0, limit - 1) + "…";

        static void AppendSingleLine(StringBuilder builder, string value)
        {
            foreach (var character in value)
                builder.Append(char.IsWhiteSpace(character) ? ' ' : character);
        }

        static void AppendStack(StringBuilder builder, string stack)
        {
            var simplified = BridgeExceptionFormatter.TrimCommonLogTail(BridgeExceptionFormatter.SimplifyStackTrace(stack));
            var remaining = simplified.AsSpan();
            var frames = 0;
            while (!remaining.IsEmpty && frames < 2)
            {
                var newline = remaining.IndexOf('\n');
                var frame = (newline < 0 ? remaining : remaining.Slice(0, newline)).Trim();
                remaining = newline < 0 ? default : remaining.Slice(newline + 1);
                if (frame.IsEmpty || frame.StartsWith("UnityEngine.Debug".AsSpan(), StringComparison.Ordinal)
                    || frame.StartsWith("UnityEngine.Logger".AsSpan(), StringComparison.Ordinal))
                    continue;

                var shortened = frame.Length > 128 || frames == 1 && !remaining.Trim().IsEmpty;
                builder.Append("\n  ").Append(frame.Slice(0, Math.Min(frame.Length, shortened ? 127 : 128)));
                if (shortened)
                    builder.Append('…');
                frames++;
            }
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
            public int Compare(Group left, Group right) => left.Order.CompareTo(right.Order);
        }
    }
}
