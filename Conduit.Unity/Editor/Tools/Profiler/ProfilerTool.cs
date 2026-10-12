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
                var rootSelector = options.GetString("root", "");
                var threadSelector = options.GetString("thread", "main");
                var thresholdText = options.GetString("threshold_ms", "");
                double? threshold = null;
                if (!string.IsNullOrEmpty(thresholdText))
                {
                    if (!double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                        || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                        throw new ArgumentException("threshold_ms must be a finite, nonnegative number.");
                    threshold = value;
                }
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
                builder.AppendLine($"Capture: {captureId}; frames: {firstFrameOrdinal}..{firstFrameOrdinal + frames.Count - 1}; Unity indices: {frames[0]}..{frames[^1]}");
                builder.Append("Sample count: ");
                builder.AppendInvariant(sampleCount).AppendLine();
                builder.AppendLine();

                AppendRangeOverview(builder, frameStats, mode, rootSelector, threadSelector, threshold, warnings);

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

                var hasReference = TryParseReference(rootSelector, out var reference);
                var warnings = new List<string>();
                var frame = hasReference ? reference.Frame : ResolveSingleFrame(frameSelector, out warnings);
                if (hasReference)
                {
                    threadSelector = reference.Thread;
                    rootSelector = reference.Key.Split('~')[0];
                    if (reference.Kind == 's')
                        return BrowseInvocation(reference, limit);
                }
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

                if (hasReference && reference.Kind == 'c')
                    return BrowseCalls(frame, threadSelector, selectedRoot, reference.Key, sort, limit);
                if (sort == "start_ms")
                    return Failure("Unable to sort hierarchy by start time.", "Open the calls reference, then use sort=start_ms.", null);

                using var pooledVisibleRows = ConduitPool.GetPooledSet<HierarchyRow>(out var visibleRows);
                SelectVisibleRows(selectedRoot, depth, sort, onlyNonTrivial, frameTimeMs, visibleRows);
                using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
                AppendFrameIdentity(builder, frame, threadSelector);
                var frameToken = GetFrameToken(frame);
                if (!string.IsNullOrEmpty(threadSummary))
                {
                    builder.AppendLine(threadSummary);
                    builder.AppendLine();
                }

                builder.AppendLine(
                    aggregateWorkerCount > 0
                        ? $"{"reference",-42}{"depth",-7}{"cpu_sum_ms",-12}{"worker_mean_ms",-16}{"worker_min_ms",-15}{"worker_max_ms",-15}{"workers",-9}{"self_ms",-9}{"gc_kb",-7}{"calls",-7}{"capacity_%",-12}name"
                        : $"{"reference",-42}{"depth",-7}{"total_ms",-12}{"self_ms",-9}{"gc_kb",-7}{"calls",-7}{"frame_%",-12}name"
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

                    AppendBrowseRow(builder, row, selectedRoot.Depth, frameTimeMs, aggregateWorkerCount,
                        FormatReference(frameToken, frame, threadSelector, 'h', row.PublicId!));
                    printed++;

                    if (aggregateWorkerCount > 0)
                        row.Children.Sort(rowComparison);
                    for (var index = row.Children.Count - 1; index >= 0; --index)
                        if (visibleRows.Contains(row.Children[index]))
                            pendingRows.Add(row.Children[index]);
                }

                if (!string.IsNullOrWhiteSpace(rootSelector))
                    AppendSelectedDetails(builder, frame, threadSelector, selectedRoot, limit);
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
            internal string Name;
            internal int ChildCount;
            internal double TotalMs;
            internal double SelfMs;
            internal double GcBytes;
            internal float FrameTimeMs;
        }

        internal sealed class HierarchyRow
        {
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
            internal List<(int Thread, int Sample)> Sources { get; } = new();
        }

    }
}
