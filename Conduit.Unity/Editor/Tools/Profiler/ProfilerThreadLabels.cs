#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Conduit
{
    readonly struct ProfilerThreadInfo
    {
        internal ProfilerThreadInfo(int index, string name, string groupName)
        {
            Index = index;
            Name = name;
            GroupName = groupName;
        }

        internal int Index { get; }
        internal string Name { get; }
        internal string GroupName { get; }
    }

    static class ProfilerThreadLabels
    {
        static readonly string?[] workerLabels = new string[256];

        internal static string? ClassifyThread(string? threadName, string? threadGroupName)
        {
            var name = (threadName ?? string.Empty).Trim();
            if (string.Equals(name, "Main Thread", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "UnityMain", StringComparison.OrdinalIgnoreCase))
                return "main";

            if (string.Equals(name, "Render Thread", StringComparison.OrdinalIgnoreCase))
                return "render";

            if (TryParseJobWorkerIndex(threadName, threadGroupName, out var workerIndex))
                return FormatWorkerLabel(workerIndex);

            return null;
        }

        internal static bool IsMainThread(ProfilerThreadInfo info)
            => string.Equals(info.Name.Trim(), "Main Thread", StringComparison.OrdinalIgnoreCase)
               || string.Equals(info.Name.Trim(), "UnityMain", StringComparison.OrdinalIgnoreCase);

        internal static bool IsRenderThread(ProfilerThreadInfo info)
            => string.Equals(info.Name.Trim(), "Render Thread", StringComparison.OrdinalIgnoreCase);

        internal static bool TryParseJobWorkerIndex(ProfilerThreadInfo info, out int workerIndex)
            => TryParseJobWorkerIndex(info.Name, info.GroupName, out workerIndex);

        static bool TryParseJobWorkerIndex(string? threadName, string? threadGroupName, out int workerIndex)
        {
            workerIndex = -1;
            if (!string.Equals((threadGroupName ?? string.Empty).Trim(), "Job", StringComparison.OrdinalIgnoreCase))
                return false;

            var name = (threadName ?? string.Empty).Trim();
            const string workerPrefix = "Worker ";
            const string jobWorkerPrefix = "Job Worker ";
            var digits = name.StartsWith(workerPrefix, StringComparison.Ordinal)
                ? name.AsSpan(workerPrefix.Length)
                : name.StartsWith(jobWorkerPrefix, StringComparison.Ordinal)
                    ? name.AsSpan(jobWorkerPrefix.Length)
                    : default;
            if (digits.IsEmpty)
                return false;

            foreach (var character in digits)
                if (character is < '0' or > '9')
                    return false;

            return int.TryParse(
                digits,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out workerIndex
            );
        }

        internal static string FormatWorkerLabel(int workerIndex)
        {
            if ((uint)workerIndex < workerLabels.Length)
                return workerLabels[workerIndex] ??=
                    $"worker{workerIndex.ToString(CultureInfo.InvariantCulture)}";

            return $"worker{workerIndex.ToString(CultureInfo.InvariantCulture)}";
        }

        internal static string FormatThreadLabels(IEnumerable<string> labels)
        {
            using var pooledSorted = ConduitPool.GetPooledList<string>(out var sorted);
            foreach (var label in labels)
                sorted.Add(label);
            sorted.Sort(CompareThreadLabels);
            return sorted.Count == 0 ? "none" : string.Join(", ", sorted);
        }

        static int CompareThreadLabels(string left, string right)
        {
            var leftRank = GetThreadLabelRank(left);
            var rightRank = GetThreadLabelRank(right);
            if (leftRank != rightRank)
                return leftRank.CompareTo(rightRank);

            if (TryParseWorkerLabel(left, out var leftWorker) && TryParseWorkerLabel(right, out var rightWorker))
                return leftWorker.CompareTo(rightWorker);

            return string.Compare(left, right, StringComparison.Ordinal);
        }

        static int GetThreadLabelRank(string label)
        {
            if (string.Equals(label, "main", StringComparison.Ordinal))
                return 0;

            if (string.Equals(label, "render", StringComparison.Ordinal))
                return 1;

            return TryParseWorkerLabel(label, out _) ? 2 : 3;
        }

        static bool TryParseWorkerLabel(string label, out int workerIndex)
        {
            workerIndex = -1;
            return label.StartsWith("worker", StringComparison.Ordinal)
                   && int.TryParse(label["worker".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out workerIndex);
        }

    }
}
