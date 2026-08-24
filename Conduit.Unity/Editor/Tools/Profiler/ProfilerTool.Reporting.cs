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

        static SampleRow ReadSampleRow(
            HierarchyFrameDataView hierarchy,
            int itemId,
            int frameIndex,
            int frameOrdinal,
            float frameTimeMs,
            string displayPath,
            int depth,
            int childCount
        )
            => new()
            {
                FrameIndex = frameIndex,
                FrameOrdinal = frameOrdinal,
                Name = hierarchy.GetItemName(itemId) ?? "<unnamed>",
                DisplayPath = displayPath,
                Depth = depth,
                ChildCount = childCount,
                TotalMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnTotalTime),
                SelfMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnSelfTime),
                GcBytes = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnGcMemory),
                Calls = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnCalls),
                FrameTimeMs = frameTimeMs,
            };

        static List<FrameStats> TopFrames(List<FrameStats> frames, Func<FrameStats, double> selector)
        {
            frames.Sort((left, right) => selector(right).CompareTo(selector(left)));
            return frames;
        }

        static int CompareOverviewSamples(SampleRow left, SampleRow right, string mode)
        {
            var leftScore = GetOverviewScore(left, mode);
            var rightScore = GetOverviewScore(right, mode);
            var scoreComparison = rightScore.CompareTo(leftScore);
            if (scoreComparison != 0)
                return scoreComparison;

            var totalComparison = right.TotalMs.CompareTo(left.TotalMs);
            if (totalComparison != 0)
                return totalComparison;

            var selfComparison = right.SelfMs.CompareTo(left.SelfMs);
            return selfComparison != 0
                ? selfComparison
                : left.DisplayPath.CompareTo(right.DisplayPath);
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
            builder.AppendLine("frame    cpu_ms    gpu_ms    fps       gc_kb    samples");
            var count = Math.Min(OverviewRowLimit, rows.Count);
            for (var index = 0; index < count; ++index)
            {
                var row = rows[index];
                AppendPadded(builder, ProfilerValueFormatter.FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex), 9);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.CpuMs), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatOptionalNumber(row.GpuMs), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.Fps), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatKb(row.GcBytes), 9);
                builder.AppendInvariant(row.SampleCount).AppendLine();
            }
        }

        static void AppendSampleTable(StringBuilder builder, IReadOnlyList<SampleRow> rows, bool includeRank = false)
        {
            builder.AppendLine(includeRank
                ? "rank  frame  total_ms  self_ms  gc_kb  calls  frame_%  sample"
                : "frame  total_ms  self_ms  gc_kb  calls  frame_%  name");
            var rank = 1;
            var count = Math.Min(OverviewRowLimit, rows.Count);
            for (var index = 0; index < count; ++index)
            {
                var row = rows[index];
                if (includeRank)
                    AppendPadded(builder, rank++, 6);

                AppendPadded(builder, ProfilerValueFormatter.FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex), 7);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.TotalMs), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.SelfMs), 9);
                AppendPadded(builder, ProfilerValueFormatter.FormatKb(row.GcBytes), 7);
                AppendPadded(builder, (int)Math.Round(row.Calls), 7);
                AppendPadded(builder, ProfilerValueFormatter.FormatPercent(row.TotalMs, row.FrameTimeMs), 9);
                builder.AppendLine(
                    includeRank
                        ? ProfilerValueFormatter.FormatSamplePath(row.DisplayPath)
                        : ProfilerValueFormatter.FormatSampleName(row.Name)
                );
            }
        }

        static void AppendBrowseRow(StringBuilder builder, HierarchyRow row, int rootDepth, float frameTimeMs, int aggregateWorkerCount)
        {
            var relativeDepth = row.Depth - rootDepth;
            var normalizedMs = aggregateWorkerCount > 0 ? GetNormalizedWorkerMs(row, aggregateWorkerCount) : row.TotalMs;
            AppendPadded(builder, row.PublicId ?? "", 10);
            AppendPadded(builder, relativeDepth, 7);
            AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.TotalMs), 10);
            if (aggregateWorkerCount > 0)
            {
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(GetWorkerMeanMs(row)), 9);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.MinTotalMs), 8);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.MaxTotalMs), 8);
                AppendPadded(builder, row.ContributingWorkerCount, 9);
            }

            AppendPadded(builder, ProfilerValueFormatter.FormatNumber(row.SelfMs), 9);
            AppendPadded(builder, ProfilerValueFormatter.FormatKb(row.GcBytes), 7);
            AppendPadded(builder, (int)Math.Round(row.Calls), 7);
            AppendPadded(builder, ProfilerValueFormatter.FormatPercent(normalizedMs, frameTimeMs), 9);
            builder.Append(' ', Math.Max(0, relativeDepth * 2));
            builder.AppendLine(ProfilerValueFormatter.FormatSampleName(row.Name));
        }

        static void AppendPadded(StringBuilder builder, string value, int width)
        {
            builder.Append(value);
            if (value.Length < width)
                builder.Append(' ', width - value.Length);
        }

        static void AppendPadded(StringBuilder builder, int value, int width)
        {
            var start = builder.Length;
            builder.AppendInvariant(value);
            var length = builder.Length - start;
            if (length < width)
                builder.Append(' ', width - length);
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
