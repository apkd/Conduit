#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Conduit
{
    static partial class ProfilerTool
    {
        internal static CapturePath ResolveCapturePathForTest(string? fileName, bool allocateDefault)
            => ResolveCapturePath(fileName, allocateDefault);

        internal static int GetAvailableFrameCountForTest() => CountAvailableFrames();

        internal static List<int> ResolveFrameRangeForTest(int frameCount, string frameRange, out List<string> warnings)
        {
            warnings = new();
            var frames = new List<int>();
            for (var i = 0; i < frameCount; i++)
                frames.Add(i + 1000);

            var separatorIndex = frameRange.IndexOf("..", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                var single = ResolveFrameEndpoint(frameRange, frames.Count);
                return single >= 0 && single < frames.Count ? new() { frames[single] } : new();
            }

            var start = ResolveFrameEndpoint(frameRange[..separatorIndex], frames.Count);
            var end = ResolveFrameEndpoint(frameRange[(separatorIndex + 2)..], frames.Count);
            start = Math.Min(frames.Count - 1, Math.Max(0, start));
            end = Math.Min(frames.Count - 1, Math.Max(0, end));
            if (end < start)
                (start, end) = (end, start);

            var count = end - start + 1;
            if (count > MaxScanFrameCount)
            {
                warnings.Add($"frame_range_clamped_to_latest_{MaxScanFrameCount.ToString(CultureInfo.InvariantCulture)}");
                start = end - MaxScanFrameCount + 1;
                count = MaxScanFrameCount;
            }

            return frames.GetRange(start, count);
        }

        internal static bool IsNonTrivialForTest(double totalMs, double selfMs, double gcBytes, double calls, float frameTimeMs, string sort)
            => IsNonTrivial(
                new()
                {
                    TotalMs = totalMs,
                    SelfMs = selfMs,
                    GcBytes = gcBytes,
                    Calls = calls,
                },
                sort,
                frameTimeMs
            );

        internal static bool ShouldIncludeOverviewSampleForTest(
            string name,
            double totalMs,
            double selfMs,
            double gcBytes,
            float frameTimeMs,
            int childCount,
            string mode
        )
            => ShouldIncludeOverviewSample(
                new()
                {
                    Name = name,
                    TotalMs = totalMs,
                    SelfMs = selfMs,
                    GcBytes = gcBytes,
                    FrameTimeMs = frameTimeMs,
                    ChildCount = childCount,
                },
                mode
            );

        internal static double GetActionableCpuMsForTest(double totalMs, double selfMs, int childCount)
            => GetActionableCpuMs(new() { TotalMs = totalMs, SelfMs = selfMs, ChildCount = childCount });

        internal static HierarchyRow AggregateWorkerHierarchiesForTest(params HierarchyRow[] workerRoots)
        {
            var aggregate = CreateWorkerAggregateRoot();
            foreach (var workerRoot in workerRoots)
                MergeWorkerHierarchy(aggregate, workerRoot);

            return aggregate;
        }

        internal static double GetWorkerMeanMsForTest(HierarchyRow row) => GetWorkerMeanMs(row);

        internal static double GetNormalizedWorkerMsForTest(HierarchyRow row, int workerCount)
            => GetNormalizedWorkerMs(row, workerCount);
    }
}
