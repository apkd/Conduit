#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Conduit
{
    static partial class ProfilerTool
    {
        static void AppendCpuOverview(StringBuilder builder, List<FrameStats> frameStats, List<string> warnings)
        {
            builder.AppendLine("Worst frames, sorted by cpu_ms:");
            AppendFrameTable(builder, TopFrames(frameStats, static stats => stats.CpuMs));
            builder.AppendLine();

            var samples = CollectMainThreadSamples(frameStats, "cpu_ms");
            builder.AppendLine("Interesting samples, sorted by actionable_cpu_ms:");
            AppendSampleTable(builder, samples, includeRank: true);
            AppendWarnings(builder, warnings);
        }

        static void AppendGcOverview(StringBuilder builder, List<FrameStats> frameStats, List<string> warnings)
        {
            builder.AppendLine("Worst frames, sorted by gc_kb:");
            AppendFrameTable(builder, TopFrames(frameStats, static stats => stats.GcBytes));
            builder.AppendLine();

            builder.AppendLine("Interesting samples, sorted by gc_kb:");
            AppendSampleTable(builder, CollectMainThreadSamples(frameStats, "gc_kb"), includeRank: true);
            AppendWarnings(builder, warnings);
        }

        static List<SampleRow> CollectMainThreadSamples(List<FrameStats> frames, string mode)
        {
            using var pooledBestByPath = ConduitPool.GetPooledDictionary<string, SampleRow>(out var bestByPath);
            // profiler hierarchies repeat across frames; share their path strings instead of rebuilding each copy.
            using var pooledIdentityPaths = ConduitPool.GetPooledDictionary<
                (string Parent, string Segment, int Occurrence),
                string
            >(out var identityPaths);
            using var pooledDisplayPaths = ConduitPool.GetPooledDictionary<
                (string Parent, string Name),
                string
            >(out var displayPaths);
            foreach (var frame in frames)
            {
                if (!TryResolveThread(frame.FrameIndex, "main", out var thread, out _))
                    continue;

                using var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(
                    frame.FrameIndex,
                    thread.Index,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnDontSort,
                    false
                );

                if (!hierarchy.valid)
                    continue;

                CollectSampleChildren(
                    hierarchy,
                    hierarchy.GetRootItemID(),
                    frame.FrameIndex,
                    frame.FrameOrdinal,
                    hierarchy.frameTimeMs,
                    bestByPath,
                    mode,
                    identityPaths,
                    displayPaths,
                    identityPath: "",
                    displayPath: "",
                    depth: 0
                );
            }

            var rows = new List<SampleRow>(bestByPath.Values);
            rows.Sort((left, right) => CompareOverviewSamples(left, right, mode));
            return rows;
        }

        static void CollectSampleChildren(
            HierarchyFrameDataView hierarchy,
            int itemId,
            int frameIndex,
            int frameOrdinal,
            float frameTimeMs,
            Dictionary<string, SampleRow> bestByPath,
            string mode,
            Dictionary<(string Parent, string Segment, int Occurrence), string> identityPaths,
            Dictionary<(string Parent, string Name), string> displayPaths,
            string identityPath,
            string displayPath,
            int depth
        )
        {
            using var pooledChildren = ConduitPool.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            using var pooledOccurrences = ConduitPool.GetPooledDictionary<string, int>(out var occurrences);
            foreach (var child in children)
            {
                var childName = hierarchy.GetItemName(child) ?? "<unnamed>";
                var segment = NormalizeIdentitySegment(childName);
                occurrences.TryGetValue(segment, out var occurrence);
                occurrences[segment] = ++occurrence;

                GetSamplePaths(
                    identityPaths,
                    displayPaths,
                    identityPath,
                    displayPath,
                    childName,
                    segment,
                    occurrence,
                    out var childIdentityPath,
                    out var childDisplayPath
                );

                CollectSampleTree(
                    hierarchy,
                    child,
                    frameIndex,
                    frameOrdinal,
                    frameTimeMs,
                    bestByPath,
                    mode,
                    identityPaths,
                    displayPaths,
                    childIdentityPath,
                    childDisplayPath,
                    depth
                );
            }
        }

        static void CollectSampleTree(
            HierarchyFrameDataView hierarchy,
            int itemId,
            int frameIndex,
            int frameOrdinal,
            float frameTimeMs,
            Dictionary<string, SampleRow> bestByPath,
            string mode,
            Dictionary<(string Parent, string Segment, int Occurrence), string> identityPaths,
            Dictionary<(string Parent, string Name), string> displayPaths,
            string identityPath,
            string displayPath,
            int depth
        )
        {
            using var pooledChildren = ConduitPool.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            var sample = ReadSampleRow(
                hierarchy,
                itemId,
                frameIndex,
                frameOrdinal,
                frameTimeMs,
                displayPath,
                depth,
                children.Count
            );
            if (ShouldIncludeOverviewSample(sample, mode)
                && (!bestByPath.TryGetValue(identityPath, out var existing)
                    || CompareOverviewSamples(sample, existing, mode) < 0))
                bestByPath[identityPath] = sample;
            if (children.Count == 0)
                return;

            using var pooledOccurrences = ConduitPool.GetPooledDictionary<string, int>(out var occurrences);
            foreach (var child in children)
            {
                var childName = hierarchy.GetItemName(child) ?? "<unnamed>";
                var segment = NormalizeIdentitySegment(childName);
                occurrences.TryGetValue(segment, out var occurrence);
                occurrences[segment] = ++occurrence;

                GetSamplePaths(
                    identityPaths,
                    displayPaths,
                    identityPath,
                    displayPath,
                    childName,
                    segment,
                    occurrence,
                    out var childIdentityPath,
                    out var childDisplayPath
                );

                CollectSampleTree(
                    hierarchy,
                    child,
                    frameIndex,
                    frameOrdinal,
                    frameTimeMs,
                    bestByPath,
                    mode,
                    identityPaths,
                    displayPaths,
                    childIdentityPath,
                    childDisplayPath,
                    depth + 1
                );
            }
        }

        static void GetSamplePaths(
            Dictionary<(string Parent, string Segment, int Occurrence), string> identityPaths,
            Dictionary<(string Parent, string Name), string> displayPaths,
            string identityPath,
            string displayPath,
            string childName,
            string segment,
            int occurrence,
            out string childIdentityPath,
            out string childDisplayPath)
        {
            var identityKey = (identityPath, segment, occurrence);
            if (!identityPaths.TryGetValue(identityKey, out childIdentityPath))
            {
                childIdentityPath = string.IsNullOrEmpty(identityPath)
                    ? $"{segment}[{occurrence.ToString(CultureInfo.InvariantCulture)}]"
                    : $"{identityPath}/{segment}[{occurrence.ToString(CultureInfo.InvariantCulture)}]";
                identityPaths.Add(identityKey, childIdentityPath);
            }

            var displayKey = (displayPath, childName);
            if (!displayPaths.TryGetValue(displayKey, out childDisplayPath))
            {
                childDisplayPath = string.IsNullOrEmpty(displayPath)
                    ? childName
                    : $"{displayPath}/{childName}";
                displayPaths.Add(displayKey, childDisplayPath);
            }
        }

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
