#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Conduit
{
    static partial class ProfilerTool
    {
        /// <summary>Accumulates present observations; absent samples contribute zero over available frames.</summary>
        internal sealed class SampleDistribution
        {
            internal readonly List<double> Values = new();
            internal double Sum;
            internal double Max;
            internal int FramesOverThreshold;

            internal void Add(double value, double cpuMs, double? thresholdMs)
            {
                Values.Add(value);
                Sum += value;
                Max = Math.Max(Max, value);
                if (thresholdMs.HasValue && cpuMs > thresholdMs.Value)
                    FramesOverThreshold++;
            }

            internal (double Mean, double P95) Summarize(int availableFrames)
            {
                if (availableFrames == 0)
                    return default;
                Values.Sort();
                // nearest-rank percentile, with implicit zeros for frames where this path was absent.
                var index = (int)Math.Ceiling(availableFrames * 0.95) - 1 - (availableFrames - Values.Count);
                return (Sum / availableFrames, index < 0 ? 0 : Values[index]);
            }
        }

        sealed class RangeSample
        {
            internal readonly SampleDistribution Distribution = new();
            internal string Path = "";
            internal string Reference = "";
            internal int WorstFrame;
            internal int WorstUnityFrame;
            internal double Rank;
            internal bool Interesting;
        }

        static void AppendRangeOverview(
            StringBuilder builder, List<FrameStats> frames, string mode, string selector,
            string thread, double? thresholdMs, List<string> warnings)
        {
            (string Path, uint Identity)? selectedIdentity = null;
            if (TryParseReference(selector, out var reference))
            {
                if (reference.Kind != 'h')
                    throw new InvalidOperationException("Use a hierarchy reference or sample name for range statistics.");
                thread = reference.Thread;
                selectedIdentity = ResolveReferencedIdentity(reference);
            }
            if (!IsThreadSelector(thread))
                throw new ArgumentException($"Invalid thread '{thread}'. Use main, render, all_workers, or worker<N>.");

            var samples = new Dictionary<(string Path, uint Identity), RangeSample>();
            var available = 0;
            foreach (var frame in frames)
            {
                if (!TryBuildBrowseHierarchy(frame.FrameIndex, thread, "total_ms", out var root,
                        out var frameMs, out _, out _, out _, includeSamples: false))
                    continue;
                available++;
                using var pooledRows = ConduitPool.GetPooledList<HierarchyRow>(out var rows);
                Flatten(root, rows);
                AssignPublicIds(rows);
                foreach (var row in rows)
                {
                    var path = BuildDisplayPath(row);
                    if (selectedIdentity.HasValue
                        ? (path, row.IdentityHash) != selectedIdentity.Value
                        : !string.IsNullOrEmpty(selector) && !MatchesSample(row, path, selector))
                        continue;
                    var sample = new SampleRow
                    {
                        Name = row.Name, TotalMs = row.TotalMs, SelfMs = row.SelfMs,
                        GcBytes = row.GcBytes, ChildCount = row.Children.Count, FrameTimeMs = frameMs,
                    };
                    if (string.IsNullOrEmpty(selector) && (ReferenceEquals(row, root) || IsOverviewContainerSample(sample)))
                        continue;
                    if (!samples.TryGetValue((path, row.IdentityHash), out var entry))
                    {
                        entry = new() { Path = path };
                        samples.Add((path, row.IdentityHash), entry);
                    }
                    var value = mode == "gc_kb" ? row.GcBytes / 1024 : row.TotalMs;
                    if (entry.Distribution.Values.Count == 0 || value > entry.Distribution.Max)
                    {
                        entry.WorstFrame = frame.FrameOrdinal;
                        entry.WorstUnityFrame = frame.FrameIndex;
                        entry.Reference = row.PublicId!;
                    }
                    entry.Distribution.Add(value, row.TotalMs, thresholdMs);
                    entry.Interesting |= !string.IsNullOrEmpty(selector) || ShouldIncludeOverviewSample(sample, mode);
                    entry.Rank = Math.Max(entry.Rank, GetOverviewScore(sample, mode));
                }
            }

            builder.AppendLine($"Sample thread: {thread}; available frames: {available}/{frames.Count}; missing: {frames.Count - available}");
            builder.AppendLine(mode == "gc_kb"
                ? "Per-frame GC totals, KB. Absent samples count as zero; missing frames are excluded."
                : "Per-frame inclusive totals, ms. Absent samples count as zero; missing frames are excluded.");
            if (thread == "all_workers")
                builder.AppendLine("Worker CPU is summed across threads; it is not elapsed frame time.");
            builder.AppendLine("p95 uses nearest rank. Paths are measured separately; parent and child totals overlap.");

            var ranked = new List<RangeSample>();
            foreach (var sample in samples.Values)
                if (sample.Interesting)
                    ranked.Add(sample);
            ranked.Sort((left, right) =>
            {
                var result = right.Rank.CompareTo(left.Rank);
                return result != 0 ? result : string.CompareOrdinal(left.Path, right.Path);
            });

            builder.Append("mean      p95       max       worst_frame  unity_frame  ");
            if (thresholdMs.HasValue)
                builder.Append($"frames>{ProfilerValueFormatter.FormatNumber(thresholdMs.Value)}ms  ");
            builder.AppendLine("reference  sample");
            for (var index = 0; index < Math.Min(OverviewRowLimit, ranked.Count); index++)
            {
                var sample = ranked[index];
                var (mean, p95) = sample.Distribution.Summarize(available);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(mean), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(p95), 10);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(sample.Distribution.Max), 10);
                AppendPadded(builder, sample.WorstFrame, 13);
                AppendPadded(builder, sample.WorstUnityFrame, 13);
                if (thresholdMs.HasValue)
                    AppendPadded(builder, $"{sample.Distribution.FramesOverThreshold}/{available}", 14);
                builder.Append(MakeReference(sample.WorstUnityFrame, thread, 'h', sample.Reference))
                    .Append("  ").AppendLine(ProfilerValueFormatter.FormatSamplePath(sample.Path));
            }
            if (ranked.Count > OverviewRowLimit)
                builder.AppendLine($"{ranked.Count - OverviewRowLimit} more paths omitted; select root to narrow the report.");
            if (ranked.Count == 0)
                builder.AppendLine("No matching samples in the available frames.");

            if (string.IsNullOrEmpty(selector))
            {
                builder.AppendLine();
                builder.AppendLine(mode == "gc_kb" ? "Worst frames by GC:" : "Worst frames by CPU:");
                AppendFrameTable(builder, TopFrames(frames, stats => mode == "gc_kb" ? stats.GcBytes : stats.CpuMs));
            }
            AppendWarnings(builder, warnings);
        }

        static bool MatchesSample(HierarchyRow row, string path, string selector)
            => string.Equals(row.Name, selector, StringComparison.OrdinalIgnoreCase)
               || string.Equals(ProfilerValueFormatter.FormatSampleName(row.Name), selector, StringComparison.OrdinalIgnoreCase)
               || string.Equals(path, selector, StringComparison.OrdinalIgnoreCase)
               || string.Equals(ProfilerValueFormatter.FormatSamplePath(path), selector, StringComparison.OrdinalIgnoreCase);

        static FrameStats ReadFrameStats(
            int frameIndex,
            int frameOrdinal,
            HashSet<string> threadLabels)
        {
            var stats = new FrameStats { FrameIndex = frameIndex, FrameOrdinal = frameOrdinal };
            for (var threadIndex = 0; ; threadIndex++)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex);
                if (!raw.valid)
                    break;

                if (threadIndex == 0)
                {
                    stats.CpuMs = raw.frameTimeMs;
                    stats.GpuMs = raw.frameGpuTimeMs;
                    stats.Fps = raw.frameFps;
                }

                stats.SampleCount += raw.sampleCount;
                stats.GcBytes += ReadGcAllocBytes(raw);
                if (ProfilerThreadLabels.ClassifyThread(raw.threadName, raw.threadGroupName) is { } label)
                    threadLabels.Add(label);
            }

            return stats;
        }

        static (double CpuMs, double GpuMs, double Fps) ReadFrameTiming(int frameIndex)
        {
            using var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, 0);
            return raw.valid
                ? (raw.frameTimeMs, raw.frameGpuTimeMs, raw.frameFps)
                : default;
        }

        static long ReadGcAllocBytes(RawFrameDataView raw)
        {
            var markerId = raw.GetMarkerId("GC.Alloc");
            if (markerId == FrameDataView.invalidMarkerId)
                return 0;

            long bytes = 0;
            for (var sampleIndex = 0; sampleIndex < raw.sampleCount; sampleIndex++)
            {
                if (raw.GetSampleMarkerId(sampleIndex) != markerId || raw.GetSampleMetadataCount(sampleIndex) == 0)
                    continue;

                bytes += raw.GetSampleMetadataAsLong(sampleIndex, 0);
            }

            return bytes;
        }

    }
}
