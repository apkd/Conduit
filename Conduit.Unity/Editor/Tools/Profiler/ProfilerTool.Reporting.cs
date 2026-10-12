#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor.Profiling;

namespace Conduit
{
    static partial class ProfilerTool
    {
        static double ReadColumn(HierarchyFrameDataView hierarchy, int itemId, int column)
            => hierarchy.GetItemColumnDataAsDouble(itemId, column);

        static List<FrameStats> TopFrames(List<FrameStats> frames, Func<FrameStats, double> selector)
        {
            frames.Sort((left, right) => selector(right).CompareTo(selector(left)));
            return frames;
        }

        static bool ShouldIncludeOverviewSample(SampleRow sample, string mode)
        {
            if (IsOverviewContainerSample(sample))
                return false;

            if (mode == "gc_kb")
                return sample.GcBytes > 0 && sample.ChildCount == 0;

            return GetActionableCpuMs(sample) >= GetOverviewThresholdMs(sample.FrameTimeMs);
        }

        static double GetOverviewScore(SampleRow sample, string mode)
            => mode == "gc_kb" ? sample.GcBytes : GetActionableCpuMs(sample);

        static double GetActionableCpuMs(SampleRow sample)
        {
            if (sample.ChildCount == 0)
                return sample.TotalMs;

            return sample.SelfMs;
        }

        static bool IsOverviewContainerSample(SampleRow sample)
        {
            var name = sample.Name.Trim();
            if (string.Equals(name, "EditorLoop", StringComparison.Ordinal)
                || string.Equals(name, "PlayerLoop", StringComparison.Ordinal)
                || string.Equals(name, "Main Thread", StringComparison.Ordinal)
                || string.Equals(name, "Render Thread", StringComparison.Ordinal))
                return true;

            return sample.ChildCount > 0
                   && sample.SelfMs <= sample.TotalMs * 0.05
                   && IsUnityLoopPhaseName(name);
        }

        static bool IsUnityLoopPhaseName(string name)
            => name switch
            {
                "Initialization" => true,
                "EarlyUpdate"    => true,
                "FixedUpdate"    => true,
                "PreUpdate"      => true,
                "Update"         => true,
                "PreLateUpdate"  => true,
                "PostLateUpdate" => true,
                "TimeUpdate"     => true,
                _                => false,
            };

        static double GetOverviewThresholdMs(float frameTimeMs)
            => Math.Max(0.001, frameTimeMs * 0.01);

        static void AppendFrameTable(StringBuilder builder, IReadOnlyList<FrameStats> rows)
        {
            builder.AppendLine("frame    unity_index  cpu_ms    gpu_ms    fps       gc_kb    samples");
            var count = Math.Min(OverviewRowLimit, rows.Count);
            for (var index = 0; index < count; ++index)
            {
                var row = rows[index];
                AppendPadded(builder, ProfilerValueFormatter.FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex), 9);
                AppendPadded(builder, row.FrameIndex, 13);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.CpuMs), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatOptionalNumber(row.GpuMs), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.Fps), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatKb(row.GcBytes), 9);
                builder.AppendInvariant(row.SampleCount).AppendLine();
            }
        }

        static void AppendBrowseRow(StringBuilder builder, HierarchyRow row, int rootDepth, float frameTimeMs, int aggregateWorkerCount, string reference)
        {
            var relativeDepth = row.Depth - rootDepth;
            var normalizedMs = aggregateWorkerCount > 0 ? GetNormalizedWorkerMs(row, aggregateWorkerCount) : row.TotalMs;
            AppendPadded(builder, reference, 42);
            AppendPadded(builder, relativeDepth, 7);
            AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.TotalMs), 12);
            if (aggregateWorkerCount > 0)
            {
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(GetWorkerMeanMs(row)), 16);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.MinTotalMs), 15);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.MaxTotalMs), 15);
                AppendPadded(builder, row.ContributingWorkerCount, 9);
            }

            AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.SelfMs), 9);
            AppendPadded(builder, ProfilerValueFormatter.FormatKb(row.GcBytes), 7);
            AppendPadded(builder, (int)Math.Round(row.Calls), 7);
            AppendPadded(builder, ProfilerValueFormatter.FormatPercent(normalizedMs, frameTimeMs), 12);
            builder.Append(' ', Math.Max(0, relativeDepth * 2));
            builder.AppendLine(ProfilerValueFormatter.FormatSampleName(row.Name));
        }

        static void AppendPadded(StringBuilder builder, string value, int width)
        {
            builder.Append(value);
            builder.Append(' ', Math.Max(1, width - value.Length));
        }

        static void AppendPadded(StringBuilder builder, int value, int width)
        {
            var start = builder.Length;
            builder.AppendInvariant(value);
            var length = builder.Length - start;
            builder.Append(' ', Math.Max(1, width - length));
        }

        static double GetWorkerMeanMs(HierarchyRow row) => row.TotalMs / row.ContributingWorkerCount;

        static double GetNormalizedWorkerMs(HierarchyRow row, int workerCount) => row.TotalMs / workerCount;

        static void AppendWarnings(StringBuilder builder, List<string> warnings)
        {
            if (warnings.Count == 0)
                return;

            builder.AppendLine();
            builder.Append("warnings: ");
            builder.AppendLine(string.Join(", ", warnings));
        }

        static Comparison<HierarchyRow> GetRowComparison(string sort)
            => sort switch
            {
                "self_ms"  => selfRowComparison,
                "gc_bytes" => gcRowComparison,
                "calls"    => callsRowComparison,
                _          => totalRowComparison,
            };

        static int CompareRowValues(
            double leftValue,
            double rightValue,
            HierarchyRow left,
            HierarchyRow right)
        {
            var result = rightValue.CompareTo(leftValue);

            return result != 0 ? result : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        }

    }
}
