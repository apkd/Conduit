#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine.Profiling;

namespace Conduit
{
    static partial class ProfilerTool
    {
        const int MaxScanFrameCount = 2000;
        const int OverviewRowLimit = 10;
        const int MaxBrowseLimit = 200;
        static readonly Comparison<HierarchyRow> totalRowComparison = static (left, right) =>
            CompareRowValues(left.TotalMs, right.TotalMs, left, right);
        static readonly Comparison<HierarchyRow> selfRowComparison = static (left, right) =>
            CompareRowValues(left.SelfMs, right.SelfMs, left, right);
        static readonly Comparison<HierarchyRow> gcRowComparison = static (left, right) =>
            CompareRowValues(left.GcBytes, right.GcBytes, left, right);
        static readonly Comparison<HierarchyRow> callsRowComparison = static (left, right) =>
            CompareRowValues(left.Calls, right.Calls, left, right);

        internal static string BuildStatusLine()
        {
            try
            {
                if (!ProfilerDriver.enabled)
                    return "Profiler: not recording";

                var firstFrame = ProfilerDriver.firstFrameIndex;
                var lastFrame = ProfilerDriver.lastFrameIndex;
                var hasFrames = firstFrame >= 0 && lastFrame >= firstFrame;
                var selected = hasFrames && TryGetSelectedFrame(firstFrame, lastFrame, out var selectedFrame)
                    ? selectedFrame.ToString(CultureInfo.InvariantCulture)
                    : "none";

                if (!hasFrames)
                    return $"Profiler: recording; frames=none; selected={selected}; latest=none; "
                           + $"total_allocated={ProfilerValueFormatter.FormatMb(Profiler.GetTotalAllocatedMemoryLong())}MB; "
                           + $"gc_reserved={ProfilerValueFormatter.FormatMb(Profiler.GetMonoHeapSizeLong())}MB; "
                           + $"system_used={ProfilerValueFormatter.FormatMb(Profiler.GetTotalReservedMemoryLong())}MB";

                var timing = ReadFrameTiming(lastFrame);
                return $"Profiler: recording; frames={firstFrame}..{lastFrame}; selected={selected}; latest={lastFrame}; "
                       + $"cpu={ProfilerValueFormatter.FormatNumber(timing.CpuMs)}ms; "
                       + $"gpu={ProfilerValueFormatter.FormatOptionalNumber(timing.GpuMs)}ms; "
                       + $"fps={ProfilerValueFormatter.FormatNumber(timing.Fps)}; "
                       + $"total_allocated={ProfilerValueFormatter.FormatMb(Profiler.GetTotalAllocatedMemoryLong())}MB; "
                       + $"gc_reserved={ProfilerValueFormatter.FormatMb(Profiler.GetMonoHeapSizeLong())}MB; "
                       + $"system_used={ProfilerValueFormatter.FormatMb(Profiler.GetTotalReservedMemoryLong())}MB";
            }
            catch (Exception exception)
            {
                return $"Profiler: unavailable; error={exception.Message}";
            }
        }

        internal static async Task<BridgeCommandResult> RecordAsync(string[] args)
        {
            var options = new ProfilerOptions(args);
            var action = options.GetString("action", "capture");
            return action switch
            {
                "capture" => await CaptureAsync(options),
                "save"    => Save(options),
                "load"    => Load(options),
                "list"    => ListCaptures(),
                _         => Failure("Unable to use profiler record action.", $"Unsupported action '{action}'.", null),
            };
        }

        internal static BridgeCommandResult Overview(string[] args)
        {
            try
            {
                var options = new ProfilerOptions(args);
                var mode = options.GetString("mode", "cpu_ms");
                var frameRange = options.GetString("frame_range", "0..^1");
                var frames = ResolveFrameRange(frameRange, out var warnings, out var firstFrameOrdinal);
                if (frames.Count == 0)
                    return BridgeCommandResult.Success("No profiler frames available. Use profiler_record action=capture first.");

                using var pooledFrameStats = ConduitPool.GetPooledList<FrameStats>(out var frameStats);
                if (frameStats.Capacity < frames.Count)
                    frameStats.Capacity = frames.Count;
                using var pooledThreadLabels = ConduitPool.GetPooledSet<string>(out var threadLabels);
                long sampleCount = 0;
                for (var frameOffset = 0; frameOffset < frames.Count; ++frameOffset)
                {
                    var stats = ReadFrameStats(
                        frames[frameOffset],
                        firstFrameOrdinal + frameOffset,
                        threadLabels
                    );
                    frameStats.Add(stats);
                    sampleCount += stats.SampleCount;
                }

                using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
                builder.Append("Threads: ");
                builder.AppendLine(ProfilerThreadLabels.FormatThreadLabels(threadLabels));
                builder.Append("Sample count: ");
                builder.AppendInvariant(sampleCount).AppendLine();
                builder.AppendLine();

                if (mode == "gc_kb")
                    AppendGcOverview(builder, frameStats, warnings);
                else
                    AppendCpuOverview(builder, frameStats, warnings);

                return BridgeCommandResult.Success(builder.ToTrimmedString());
            }
            catch (Exception exception)
            {
                return Failure("Unable to create profiler overview.", exception.Message, null);
            }
        }

        internal static BridgeCommandResult Browse(string[] args)
        {
            try
            {
                var options = new ProfilerOptions(args);
                var frameSelector = options.GetString("frame", "selected");
                var threadSelector = options.GetString("thread", "main");
                var rootSelector = options.GetString("root", "");
                var depth = options.GetInt("depth", 3, 1, 32);
                var sort = options.GetString("sort", "total_ms");
                var limit = options.GetInt("limit", 50, 1, MaxBrowseLimit);
                var onlyNonTrivial = options.GetBool("only_non_trivial", true);

                var frame = ResolveSingleFrame(frameSelector, out var warnings);
                if (frame < 0)
                    return BridgeCommandResult.Success("No profiler frames available. Use profiler_record action=capture first.");

                if (!TryBuildBrowseHierarchy(
                        frame,
                        threadSelector,
                        sort,
                        out var root,
                        out var frameTimeMs,
                        out var aggregateWorkerCount,
                        out var threadSummary,
                        out var threadDiagnostic
                    ))
                    return Failure("Unable to browse profiler hierarchy.", threadDiagnostic, null);

                using var pooledRows = ConduitPool.GetPooledList<HierarchyRow>(out var rows);
                Flatten(root, rows);
                AssignPublicIds(rows);
                var selectedRoot = ResolveRoot(root, rows, rootSelector, warnings);
                if (selectedRoot == null)
                    return Failure("Unable to browse profiler hierarchy.", $"Root '{rootSelector}' was not found.", null);

                using var pooledVisibleRows = ConduitPool.GetPooledSet<HierarchyRow>(out var visibleRows);
                SelectVisibleRows(selectedRoot, depth, sort, onlyNonTrivial, frameTimeMs, visibleRows);
                using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
                if (!string.IsNullOrEmpty(threadSummary))
                {
                    builder.AppendLine(threadSummary);
                    builder.AppendLine();
                }

                builder.AppendLine(
                    aggregateWorkerCount > 0
                        ? "id        depth  total_ms  mean_ms  min_ms  max_ms  workers  self_ms  gc_kb  calls  frame_%  name"
                        : "id        depth  total_ms  self_ms  gc_kb  calls  frame_%  name"
                );

                var printed = 0;
                var rowComparison = GetRowComparison(sort);
                using var pooledPendingRows = ConduitPool.GetPooledList<HierarchyRow>(out var pendingRows);
                pendingRows.Add(selectedRoot);
                while (pendingRows.Count > 0)
                {
                    var lastIndex = pendingRows.Count - 1;
                    var row = pendingRows[lastIndex];
                    pendingRows.RemoveAt(lastIndex);
                    if (!visibleRows.Contains(row))
                        continue;

                    if (printed >= limit)
                    {
                        warnings.Add("row_limit_reached");
                        break;
                    }

                    AppendBrowseRow(builder, row, selectedRoot.Depth, frameTimeMs, aggregateWorkerCount);
                    printed++;

                    if (aggregateWorkerCount > 0)
                        row.Children.Sort(rowComparison);
                    for (var index = row.Children.Count - 1; index >= 0; --index)
                        if (visibleRows.Contains(row.Children[index]))
                            pendingRows.Add(row.Children[index]);
                }

                AppendWarnings(builder, warnings);
                return BridgeCommandResult.Success(builder.ToTrimmedString());
            }
            catch (Exception exception)
            {
                return Failure("Unable to browse profiler hierarchy.", exception.Message, null);
            }
        }

        internal static bool HasMarker(string markerName)
        {
            if (string.IsNullOrWhiteSpace(markerName))
                return false;

            var firstFrame = ProfilerDriver.firstFrameIndex;
            var lastFrame = ProfilerDriver.lastFrameIndex;
            if (firstFrame < 0 || lastFrame < firstFrame)
                return false;

            Span<int> recentFrames = stackalloc int[10];
            var frameCount = 0;
            for (var frame = firstFrame; ;)
            {
                recentFrames[frameCount++ % recentFrames.Length] = frame;
                if (frame == lastFrame)
                    break;

                var nextFrame = ProfilerDriver.GetNextFrameIndex(frame);
                if (nextFrame <= frame)
                    break;
                frame = nextFrame;
            }

            var recentFrameCount = Math.Min(frameCount, recentFrames.Length);
            for (var offset = 0; offset < recentFrameCount; offset++)
            {
                var frame = recentFrames[(frameCount - offset - 1) % recentFrames.Length];
                for (var threadIndex = 0; ; threadIndex++)
                {
                    using var raw = ProfilerDriver.GetRawFrameDataView(
                        frame,
                        threadIndex
                    );
                    if (!raw.valid)
                        break;

                    if (raw.GetMarkerId(markerName) != FrameDataView.invalidMarkerId)
                        return true;
                }
            }

            return false;
        }

        static BridgeCommandResult Failure(string title, string error, string? file)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.AppendLine(title);
            builder.Append("Error: ");
            builder.AppendLine(error);
            if (!string.IsNullOrWhiteSpace(file))
            {
                builder.Append("File: ");
                builder.AppendLine(file);
            }

            return new()
            {
                outcome = ToolOutcome.Exception,
                return_value = builder.ToTrimmedString(),
            };
        }

        internal readonly struct CapturePath
        {
            internal CapturePath(string absolutePath, string displayPath)
            {
                AbsolutePath = absolutePath;
                DisplayPath = displayPath;
            }

            internal string AbsolutePath { get; }
            internal string DisplayPath { get; }
        }

        struct FrameStats
        {
            internal int FrameIndex;
            internal int FrameOrdinal;
            internal double CpuMs;
            internal double GpuMs;
            internal double Fps;
            internal long SampleCount;
            internal double GcBytes;
        }

        struct SampleRow
        {
            internal int FrameIndex;
            internal int FrameOrdinal;
            internal string Name;
            internal string DisplayPath;
            internal int Depth;
            internal int ChildCount;
            internal double TotalMs;
            internal double SelfMs;
            internal double GcBytes;
            internal double Calls;
            internal float FrameTimeMs;
        }

        internal sealed class HierarchyRow
        {
            internal int ItemId;
            internal HierarchyRow? Parent;
            internal string? PublicId;
            internal string Name = string.Empty;
            internal uint IdentityHash;
            internal int Depth;
            internal double TotalMs;
            internal double SelfMs;
            internal double GcBytes;
            internal double Calls;
            internal int ContributingWorkerCount;
            internal double MinTotalMs;
            internal double MaxTotalMs;
            internal List<HierarchyRow> Children { get; } = new();
            internal Dictionary<string, HierarchyRow>? MergedChildren;
        }

    }
}
