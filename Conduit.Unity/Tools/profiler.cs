#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace Conduit
{
    static class profiler
    {
        const int DefaultCaptureFrameCount = 120;
        const int MaxCaptureFrameCount = 600;
        const double DefaultCaptureDelaySeconds = 1;
        const double MaxCaptureDelaySeconds = 60;
        const int MaxScanFrameCount = 2000;
        const int OverviewRowLimit = 10;
        const int MaxBrowseLimit = 200;
        const string CaptureDirectory = "Temp/profiler";
        static readonly string?[] jobWorkerLabels = new string[256];
        static readonly MethodInfo? setMaxFrameHistoryLengthMethod = typeof(ProfilerDriver).GetMethod(
            "SetMaxFrameHistoryLength",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        static readonly PropertyInfo? configuredFrameHistoryLengthProperty = Type
            .GetType("UnityEditor.Profiling.ProfilerUserSettings,UnityEditor.CoreModule")
            ?.GetProperty("frameCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Comparison<HierarchyRow> totalRowComparison = static (left, right) =>
            CompareRowValues(left.TotalMs, right.TotalMs, left, right);
        static readonly Comparison<HierarchyRow> selfRowComparison = static (left, right) =>
            CompareRowValues(left.SelfMs, right.SelfMs, left, right);
        static readonly Comparison<HierarchyRow> gcRowComparison = static (left, right) =>
            CompareRowValues(left.GcBytes, right.GcBytes, left, right);
        static readonly Comparison<HierarchyRow> callsRowComparison = static (left, right) =>
            CompareRowValues(left.Calls, right.Calls, left, right);

        public static string BuildStatusLine()
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
                    return $"Profiler: recording; frames=none; selected={selected}; latest=none; total_allocated={FormatMb(Profiler.GetTotalAllocatedMemoryLong())}MB; gc_reserved={FormatMb(Profiler.GetMonoHeapSizeLong())}MB; system_used={FormatMb(Profiler.GetTotalReservedMemoryLong())}MB";

                var timing = ReadFrameTiming(lastFrame);
                return $"Profiler: recording; frames={firstFrame}..{lastFrame}; selected={selected}; latest={lastFrame}; cpu={FormatNumber(timing.CpuMs)}ms; gpu={FormatOptionalNumber(timing.GpuMs)}ms; fps={FormatNumber(timing.Fps)}; total_allocated={FormatMb(Profiler.GetTotalAllocatedMemoryLong())}MB; gc_reserved={FormatMb(Profiler.GetMonoHeapSizeLong())}MB; system_used={FormatMb(Profiler.GetTotalReservedMemoryLong())}MB";
            }
            catch (Exception exception)
            {
                return $"Profiler: unavailable; error={exception.Message}";
            }
        }

        public static async Task<BridgeCommandResult> RecordAsync(string[] args)
        {
            var options = ParseArgs(args);
            var action = GetOption(options, "action", "capture");
            return action switch
            {
                "capture" => await CaptureAsync(options),
                "save"    => Save(options),
                "load"    => Load(options),
                "list"    => ListCaptures(),
                _         => Failure("Unable to use profiler record action.", $"Unsupported action '{action}'.", null),
            };
        }

        public static BridgeCommandResult Overview(string[] args)
        {
            try
            {
                var options = ParseArgs(args);
                var mode = GetOption(options, "mode", "cpu_ms");
                var frameRange = GetOption(options, "frame_range", "0..^1");
                var frames = ResolveFrameRange(frameRange, out var warnings, out var firstFrameOrdinal);
                if (frames.Count == 0)
                    return Success("No profiler frames available. Use profiler_record action=capture first.");

                using var pooledFrameStats = ConduitUtility.GetPooledList<FrameStats>(out var frameStats);
                if (frameStats.Capacity < frames.Count)
                    frameStats.Capacity = frames.Count;
                using var pooledThreadLabels = ConduitUtility.GetPooledSet<string>(out var threadLabels);
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

                using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
                builder.Append("Threads: ");
                builder.AppendLine(FormatThreadLabels(threadLabels));
                builder.Append("Sample count: ");
                builder.AppendInvariant(sampleCount).AppendLine();
                builder.AppendLine();

                if (mode == "gc_kb")
                    AppendGcOverview(builder, frameStats, warnings);
                else
                    AppendCpuOverview(builder, frameStats, warnings);

                return Success(builder.TrimEnd().ToString());
            }
            catch (Exception exception)
            {
                return Failure("Unable to create profiler overview.", exception.Message, null);
            }
        }

        public static BridgeCommandResult Browse(string[] args)
        {
            try
            {
                var options = ParseArgs(args);
                var frameSelector = GetOption(options, "frame", "selected");
                var threadSelector = GetOption(options, "thread", "main");
                var rootSelector = GetOption(options, "root", "");
                var depth = Clamp(ParseInt(GetOption(options, "depth", "3"), 3), 1, 32);
                var sort = GetOption(options, "sort", "total_ms");
                var limit = Clamp(ParseInt(GetOption(options, "limit", "50"), 50), 1, MaxBrowseLimit);
                var onlyNonTrivial = ParseBool(GetOption(options, "only_non_trivial", "true"), true);

                var frame = ResolveSingleFrame(frameSelector, out var warnings);
                if (frame < 0)
                    return Success("No profiler frames available. Use profiler_record action=capture first.");

                if (!TryBuildBrowseHierarchy(frame, threadSelector, sort, out var root, out var frameTimeMs, out var aggregateWorkerCount, out var threadSummary, out var threadDiagnostic))
                    return Failure("Unable to browse profiler hierarchy.", threadDiagnostic, null);

                using var pooledRows = ConduitUtility.GetPooledList<HierarchyRow>(out var rows);
                Flatten(root, rows);
                AssignPublicIds(rows);
                var selectedRoot = ResolveRoot(root, rows, rootSelector, warnings);
                if (selectedRoot == null)
                    return Failure("Unable to browse profiler hierarchy.", $"Root '{rootSelector}' was not found.", null);

                using var pooledVisibleRows = ConduitUtility.GetPooledSet<HierarchyRow>(out var visibleRows);
                SelectVisibleRows(selectedRoot, depth, sort, onlyNonTrivial, frameTimeMs, visibleRows);
                using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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
                using var pooledPendingRows = ConduitUtility.GetPooledList<HierarchyRow>(out var pendingRows);
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
                return Success(builder.TrimEnd().ToString());
            }
            catch (Exception exception)
            {
                return Failure("Unable to browse profiler hierarchy.", exception.Message, null);
            }
        }

        public static bool HasMarker(string markerName)
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

        static async Task<BridgeCommandResult> CaptureAsync(Dictionary<string, string> options)
        {
            var frames = Clamp(ParseInt(GetOption(options, "frames", DefaultCaptureFrameCount.ToString(CultureInfo.InvariantCulture)), DefaultCaptureFrameCount), 1, MaxCaptureFrameCount);
            var delaySeconds = Clamp(ParseDouble(GetOption(options, "delay_seconds", DefaultCaptureDelaySeconds.ToString(CultureInfo.InvariantCulture)), DefaultCaptureDelaySeconds), 0, MaxCaptureDelaySeconds);
            var target = GetOption(options, "target", "play_mode");
            var fileName = GetOption(options, "file_name", "");
            if (!TryValidateTarget(target, out var targetDiagnostic))
                return Failure("Unable to capture profile.", targetDiagnostic, null);

            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

            var previousProfileEditor = ProfilerDriver.profileEditor;
            var previousFrameHistoryLength = GetConfiguredFrameHistoryLength();
            var outputPath = string.IsNullOrWhiteSpace(fileName)
                ? (CapturePath?)null
                : ResolveCapturePath(fileName, allocateDefault: false);
            var boundedCapturePath = outputPath?.AbsolutePath
                ?? Path.Combine(
                    ConduitAssetPathUtility.GetProjectRootPath(),
                    CaptureDirectory,
                    $".capture_{Guid.NewGuid():N}.data"
                );
            var capturedFrames = 0;
            try
            {
                try
                {
                    ProfilerDriver.enabled = false;
                    ProfilerDriver.profileEditor = target == "edit_mode";
                    SetMaxFrameHistoryLength(frames);
                    ProfilerDriver.ClearAllFrames();
                    ProfilerDriver.enabled = true;

                    var deadlineUtc = DateTime.UtcNow + BuildCaptureTimeout(frames);
                    while (CountAvailableFrames() < frames && DateTime.UtcNow < deadlineUtc)
                        await Task.Delay(50);

                    ProfilerDriver.enabled = false;
                    capturedFrames = CountAvailableFrames();
                    if (capturedFrames >= frames)
                        SaveProfile(boundedCapturePath);
                }
                finally
                {
                    ProfilerDriver.enabled = false;
                    ProfilerDriver.profileEditor = previousProfileEditor;
                    SetMaxFrameHistoryLength(previousFrameHistoryLength);
                }

                if (capturedFrames < frames)
                    return Failure("Unable to capture profile.", $"Captured {capturedFrames} of {frames} requested frames before the internal capture deadline.", null);

                if (!ProfilerDriver.LoadProfile(boundedCapturePath, false))
                    return Failure("Unable to capture profile.", "Unity could not restore the bounded profiler history.", outputPath?.DisplayPath);

                capturedFrames = CountAvailableFrames();
                if (outputPath is { } path)
                {
                    return Success(
                        $"Profile captured and saved!\nFrame count: {capturedFrames.ToString(CultureInfo.InvariantCulture)}\nFile: {path.DisplayPath}"
                    );
                }

                return Success(
                    $"Profile captured!\nFrame count: {capturedFrames.ToString(CultureInfo.InvariantCulture)}"
                );
            }
            finally
            {
                if (outputPath is null && File.Exists(boundedCapturePath))
                    File.Delete(boundedCapturePath);
            }
        }

        static int GetConfiguredFrameHistoryLength()
            => configuredFrameHistoryLengthProperty?.GetValue(null) is int frameCount
                ? frameCount
                : MaxScanFrameCount;

        static void SetMaxFrameHistoryLength(int frameCount)
            => setMaxFrameHistoryLengthMethod?.Invoke(null, new object[] { frameCount });

        static BridgeCommandResult Save(Dictionary<string, string> options)
        {
            var fileName = GetOption(options, "file_name", "");
            var path = ResolveCapturePath(fileName, allocateDefault: true);
            try
            {
                if (CountAvailableFrames() == 0)
                    return Failure("Unable to save profile capture.", "No profiler frames are available.", path.DisplayPath);

                SaveProfile(path.AbsolutePath);
                return Success($"Profile capture saved!\nFile: {path.DisplayPath}");
            }
            catch (Exception exception)
            {
                return Failure("Unable to save profile capture.", exception.Message, path.DisplayPath);
            }
        }

        static BridgeCommandResult Load(Dictionary<string, string> options)
        {
            var fileName = GetOption(options, "file_name", "");
            var path = ResolveCapturePath(fileName, allocateDefault: false);
            try
            {
                if (!File.Exists(path.AbsolutePath))
                    return Failure("Unable to load profile capture.", "File not found.", path.DisplayPath);

                if (!ProfilerDriver.LoadProfile(path.AbsolutePath, false))
                    return Failure("Unable to load profile capture.", "Unity failed to load the profile capture.", path.DisplayPath);

                return Success(
                    $"Profile capture loaded!\nFile: {path.DisplayPath}\nFrame count: {CountAvailableFrames().ToString(CultureInfo.InvariantCulture)}"
                );
            }
            catch (Exception exception)
            {
                return Failure("Unable to load profile capture.", exception.Message, path.DisplayPath);
            }
        }

        static BridgeCommandResult ListCaptures()
        {
            var directory = Path.Combine(ConduitAssetPathUtility.GetProjectRootPath(), CaptureDirectory);
            if (!Directory.Exists(directory))
                return Success($"No profile captures found.\nDirectory: {CaptureDirectory}");

            var files = Directory.GetFiles(directory, "*.data");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length == 0)
                return Success($"No profile captures found.\nDirectory: {CaptureDirectory}");

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine("Profile captures:");
            foreach (var file in files)
                builder.AppendLine(ToDisplayPath(file));

            return Success(builder.TrimEnd().ToString());
        }

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
            using var pooledBestByPath = ConduitUtility.GetPooledDictionary<string, SampleRow>(out var bestByPath);
            // profiler hierarchies repeat across frames; share their path strings instead of rebuilding each copy.
            using var pooledIdentityPaths = ConduitUtility.GetPooledDictionary<
                (string Parent, string Segment, int Occurrence),
                string
            >(out var identityPaths);
            using var pooledDisplayPaths = ConduitUtility.GetPooledDictionary<
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
            using var pooledChildren = ConduitUtility.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            using var pooledOccurrences = ConduitUtility.GetPooledDictionary<string, int>(out var occurrences);
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
            using var pooledChildren = ConduitUtility.GetPooledList<int>(out var children);
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

            using var pooledOccurrences = ConduitUtility.GetPooledDictionary<string, int>(out var occurrences);
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
                if (ClassifyThread(raw.threadName, raw.threadGroupName) is { } label)
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

        static bool TryBuildBrowseHierarchy(
            int frameIndex,
            string threadSelector,
            string sort,
            out HierarchyRow root,
            out float frameTimeMs,
            out int aggregateWorkerCount,
            out string threadSummary,
            out string diagnostic
        )
        {
            if (string.Equals(threadSelector, "all_workers", StringComparison.OrdinalIgnoreCase))
                return TryBuildWorkerHierarchy(frameIndex, sort, out root, out frameTimeMs, out aggregateWorkerCount, out threadSummary, out diagnostic);

            root = null!;
            frameTimeMs = 0;
            aggregateWorkerCount = 0;
            threadSummary = string.Empty;
            if (!TryResolveThread(frameIndex, threadSelector, out var thread, out diagnostic))
                return false;

            using var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(
                frameIndex,
                thread.Index,
                HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                HierarchyFrameDataView.columnDontSort,
                false
            );

            if (!hierarchy.valid)
            {
                diagnostic = $"No hierarchy data is available for frame {frameIndex} thread {thread.Index}.";
                return false;
            }

            root = BuildHierarchy(hierarchy, sort);
            frameTimeMs = hierarchy.frameTimeMs;
            diagnostic = string.Empty;
            return true;
        }

        static bool TryBuildWorkerHierarchy(
            int frameIndex,
            string sort,
            out HierarchyRow root,
            out float frameTimeMs,
            out int aggregateWorkerCount,
            out string threadSummary,
            out string diagnostic
        )
        {
            root = CreateWorkerAggregateRoot();
            frameTimeMs = 0;
            aggregateWorkerCount = 0;
            threadSummary = string.Empty;
            using var pooledLabels = ConduitUtility.GetPooledList<string>(out var labels);
            for (var threadIndex = 0; ; ++threadIndex)
            {
                ThreadInfo thread;
                using (var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                {
                    if (!raw.valid)
                        break;

                    thread = new()
                    {
                        Index = threadIndex,
                        Name = raw.threadName ?? $"Thread {threadIndex.ToString(CultureInfo.InvariantCulture)}",
                        GroupName = raw.threadGroupName ?? string.Empty,
                    };
                }

                if (!TryParseJobWorkerIndex(thread, out var workerIndex))
                    continue;

                using var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(
                    frameIndex,
                    thread.Index,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnDontSort,
                    false
                );

                if (!hierarchy.valid)
                    continue;

                if (labels.Count == 0)
                    frameTimeMs = hierarchy.frameTimeMs;

                MergeWorkerHierarchy(root, BuildHierarchy(hierarchy, sort));
                labels.Add(FormatWorkerLabel(workerIndex));
            }

            if (labels.Count == 0)
            {
                diagnostic = $"No Job Worker hierarchy data is available for frame {frameIndex}.";
                return false;
            }

            aggregateWorkerCount = labels.Count;
            threadSummary =
                $"Threads: {FormatThreadLabels(labels)}\n" +
                $"Aggregation: {labels.Count.ToString(CultureInfo.InvariantCulture)} Job Worker threads; total/self/GC/calls are summed, mean/min/max use workers containing each path.";
            diagnostic = string.Empty;
            return true;
        }

        static HierarchyRow CreateWorkerAggregateRoot() =>
            new()
            {
                Name = "Job Workers",
                IdentityHash = StableHash("Job Workers[1]"),
            };

        static void MergeWorkerHierarchy(HierarchyRow aggregate, HierarchyRow workerRoot)
        {
            AddWorkerMetrics(aggregate, workerRoot);

            // worker-specific roots are discarded so equivalent job paths merge
            foreach (var child in workerRoot.Children)
                MergeHierarchyRow(aggregate, child);
        }

        static void MergeHierarchyRow(HierarchyRow parent, HierarchyRow source)
        {
            // all worker trees share this index so merging stays linear in the number of samples.
            var childrenByName = parent.MergedChildren ??= new(StringComparer.Ordinal);
            if (!childrenByName.TryGetValue(source.Name, out var target))
            {
                target = new()
                {
                    Parent = parent,
                    Name = source.Name,
                    Depth = parent.Depth + 1,
                    IdentityHash = AppendIdentitySegment(
                        parent.IdentityHash,
                        NormalizeIdentitySegment(source.Name),
                        1,
                        includeSeparator: true
                    ),
                };
                parent.Children.Add(target);
                childrenByName.Add(source.Name, target);
            }

            AddWorkerMetrics(target, source);
            foreach (var child in source.Children)
                MergeHierarchyRow(target, child);
        }

        static void AddWorkerMetrics(HierarchyRow target, HierarchyRow source)
        {
            target.MinTotalMs = target.ContributingWorkerCount == 0
                ? source.TotalMs
                : Math.Min(target.MinTotalMs, source.TotalMs);
            target.MaxTotalMs = Math.Max(target.MaxTotalMs, source.TotalMs);
            target.ContributingWorkerCount++;
            target.TotalMs += source.TotalMs;
            target.SelfMs += source.SelfMs;
            target.GcBytes += source.GcBytes;
            target.Calls += source.Calls;
        }

        static HierarchyRow BuildHierarchy(HierarchyFrameDataView hierarchy, string sort)
            => BuildHierarchyRow(
                hierarchy,
                hierarchy.GetRootItemID(),
                parent: null,
                depth: 0,
                identityHash: 0,
                GetRowComparison(sort),
                knownName: null
            );

        static HierarchyRow BuildHierarchyRow(
            HierarchyFrameDataView hierarchy,
            int itemId,
            HierarchyRow? parent,
            int depth,
            uint identityHash,
            Comparison<HierarchyRow> rowComparison,
            string? knownName
        )
        {
            var name = knownName ?? hierarchy.GetItemName(itemId) ?? "<unnamed>";
            if (parent == null)
                identityHash = AppendIdentitySegment(
                    2166136261,
                    NormalizeIdentitySegment(name),
                    1,
                    includeSeparator: false
                );

            var row = new HierarchyRow
            {
                ItemId = itemId,
                Parent = parent,
                Name = name,
                Depth = depth,
                IdentityHash = identityHash,
                TotalMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnTotalTime),
                SelfMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnSelfTime),
                GcBytes = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnGcMemory),
                Calls = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnCalls),
            };

            using var pooledChildren = ConduitUtility.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            if (children.Count > 0)
            {
                using var pooledOccurrences = ConduitUtility.GetPooledDictionary<string, int>(out var occurrences);
                foreach (var childId in children)
                {
                    var childName = hierarchy.GetItemName(childId) ?? "<unnamed>";
                    var segment = NormalizeIdentitySegment(childName);
                    occurrences.TryGetValue(segment, out var occurrence);
                    occurrences[segment] = ++occurrence;

                    row.Children.Add(
                        BuildHierarchyRow(
                            hierarchy,
                            childId,
                            row,
                            depth + 1,
                            AppendIdentitySegment(
                                row.IdentityHash,
                                segment,
                                occurrence,
                                includeSeparator: true
                            ),
                            rowComparison,
                            childName
                        )
                    );
                }
            }

            row.Children.Sort(rowComparison);
            return row;
        }

        static void AssignPublicIds(IReadOnlyList<HierarchyRow> rows)
        {
            using var pooledUsed = ConduitUtility.GetPooledDictionary<string, int>(out var used);
            foreach (var row in rows)
            {
                var baseId = StableId(row.IdentityHash);
                used.TryGetValue(baseId, out var count);
                used[baseId] = ++count;
                row.PublicId = count == 1 ? baseId : $"{baseId}-{count.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        static HierarchyRow? ResolveRoot(
            HierarchyRow root,
            IReadOnlyList<HierarchyRow> rows,
            string selector,
            List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return root;

            foreach (var row in rows)
                if (string.Equals(row.PublicId, selector, StringComparison.Ordinal))
                    return row;

            if (selector.IndexOf('/') >= 0)
                foreach (var row in rows)
                    if (string.Equals(BuildDisplayPath(row), selector, StringComparison.OrdinalIgnoreCase))
                        return row;

            using var pooledExactMatches = ConduitUtility.GetPooledList<HierarchyRow>(out var exactMatches);
            foreach (var row in rows)
                if (string.Equals(row.Name, selector, StringComparison.Ordinal))
                    exactMatches.Add(row);

            if (exactMatches.Count == 0)
                foreach (var row in rows)
                    if (string.Equals(row.Name, selector, StringComparison.OrdinalIgnoreCase))
                        exactMatches.Add(row);

            if (exactMatches.Count == 0)
                return null;

            exactMatches.Sort((left, right) => right.TotalMs.CompareTo(left.TotalMs));
            if (exactMatches.Count > 1)
                warnings.Add("root_matched_multiple_items_used_highest_total_ms");

            return exactMatches[0];
        }

        static void SelectVisibleRows(
            HierarchyRow root,
            int depth,
            string sort,
            bool onlyNonTrivial,
            float frameTimeMs,
            HashSet<HierarchyRow> visibleRows)
        {
            Mark(root, 0);

            bool Mark(HierarchyRow row, int relativeDepth)
            {
                if (relativeDepth >= depth)
                    return false;

                var selfVisible = ReferenceEquals(row, root) || !onlyNonTrivial || IsNonTrivial(row, sort, frameTimeMs);
                var childVisible = false;
                foreach (var child in row.Children)
                    childVisible |= Mark(child, relativeDepth + 1);

                if (!selfVisible && !childVisible)
                    return false;

                visibleRows.Add(row);
                return true;
            }
        }

        static bool IsNonTrivial(HierarchyRow row, string sort, float frameTimeMs)
        {
            var thresholdMs = Math.Max(0.001, frameTimeMs * 0.01);
            return sort switch
            {
                "self_ms"  => row.SelfMs >= thresholdMs,
                "gc_bytes" => row.GcBytes > 0,
                "calls"    => row.Calls > 1 || row.TotalMs >= thresholdMs,
                _          => row.TotalMs >= thresholdMs,
            };
        }

        static int ResolveSingleFrame(string selector, out List<string> warnings)
        {
            warnings = new();
            var frames = GetAvailableFrames();
            if (frames.Count == 0)
                return -1;

            if (string.Equals(selector, "latest", StringComparison.OrdinalIgnoreCase))
                return frames[^1];

            if (string.Equals(selector, "selected", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetSelectedFrame(frames, out var selectedFrame))
                    return selectedFrame;

                warnings.Add("selected_frame_unavailable_used_latest");
                return frames[^1];
            }

            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal)
                && ordinal >= 0
                && ordinal < frames.Count)
                return frames[ordinal];

            throw new InvalidOperationException($"Frame selector '{selector}' did not match an available profiler frame.");
        }

        static List<int> ResolveFrameRange(
            string frameRange,
            out List<string> warnings,
            out int firstFrameOrdinal)
        {
            warnings = new();
            firstFrameOrdinal = 0;
            var frames = GetAvailableFrames();
            if (frames.Count == 0)
                return frames;

            var separatorIndex = frameRange.IndexOf("..", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                var single = ResolveFrameEndpoint(frameRange, frames.Count);
                firstFrameOrdinal = single;
                if (single < 0 || single >= frames.Count)
                {
                    frames.Clear();
                    return frames;
                }

                var selectedFrame = frames[single];
                frames.Clear();
                frames.Add(selectedFrame);
                return frames;
            }

            var start = ResolveFrameEndpoint(frameRange[..separatorIndex], frames.Count);
            var end = ResolveFrameEndpoint(frameRange[(separatorIndex + 2)..], frames.Count);
            start = Clamp(start, 0, frames.Count - 1);
            end = Clamp(end, 0, frames.Count - 1);
            if (end < start)
                (start, end) = (end, start);

            var count = end - start + 1;
            if (count > MaxScanFrameCount)
            {
                warnings.Add($"frame_range_clamped_to_latest_{MaxScanFrameCount.ToString(CultureInfo.InvariantCulture)}");
                start = end - MaxScanFrameCount + 1;
                count = MaxScanFrameCount;
            }

            firstFrameOrdinal = start;
            if (start + count < frames.Count)
                frames.RemoveRange(start + count, frames.Count - start - count);
            if (start > 0)
                frames.RemoveRange(0, start);
            return frames;
        }

        static int ResolveFrameEndpoint(string endpoint, int frameCount)
        {
            endpoint = endpoint.Trim();
            if (endpoint.StartsWith("^", StringComparison.Ordinal))
            {
                if (!int.TryParse(
                        endpoint.AsSpan(1),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var distance
                    ))
                    throw new InvalidOperationException(
                        $"Frame selector '{endpoint}' did not match an available profiler frame."
                    );

                return frameCount - distance;
            }

            if (int.TryParse(
                    endpoint,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var ordinal
                ))
                return ordinal;

            throw new InvalidOperationException(
                $"Frame selector '{endpoint}' did not match an available profiler frame."
            );
        }

        static bool TryGetSelectedFrame(List<int> frames, out int selectedFrame)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<ProfilerWindow>())
            {
                var candidate = window.selectedFrameIndex;
                if (candidate < int.MinValue || candidate > int.MaxValue)
                    continue;

                selectedFrame = (int)candidate;
                if (frames.Contains(selectedFrame))
                    return true;
            }

            selectedFrame = -1;
            return false;
        }

        static bool TryGetSelectedFrame(int firstFrame, int lastFrame, out int selectedFrame)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<ProfilerWindow>())
            {
                var candidate = window.selectedFrameIndex;
                if (candidate < firstFrame || candidate > lastFrame)
                    continue;

                var frame = firstFrame;
                while (frame >= 0 && frame <= candidate)
                {
                    if (frame == candidate)
                    {
                        selectedFrame = (int)candidate;
                        return true;
                    }

                    var next = ProfilerDriver.GetNextFrameIndex(frame);
                    if (next <= frame)
                        break;

                    frame = next;
                }
            }

            selectedFrame = -1;
            return false;
        }

        static bool TryResolveThread(int frameIndex, string selector, out ThreadInfo thread, out string diagnostic)
        {
            var matchMain = string.Equals(selector, "main", StringComparison.OrdinalIgnoreCase);
            var matchRender = string.Equals(selector, "render", StringComparison.OrdinalIgnoreCase);
            var requestedWorkerIndex = 0;
            var matchWorker = selector.StartsWith("worker", StringComparison.OrdinalIgnoreCase)
                              && int.TryParse(
                                  selector.AsSpan("worker".Length),
                                  NumberStyles.Integer,
                                  CultureInfo.InvariantCulture,
                                  out requestedWorkerIndex
                              );
            if (!matchMain && !matchRender && !matchWorker)
            {
                thread = default;
                diagnostic = $"Profiler thread '{selector}' was not found. Use main, render, all_workers, or worker<N>.";
                return false;
            }

            var hasThreads = false;
            for (var threadIndex = 0; ; ++threadIndex)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex);
                if (!raw.valid)
                    break;

                hasThreads = true;
                var candidate = new ThreadInfo
                {
                    Index = threadIndex,
                    Name = raw.threadName ?? $"Thread {threadIndex.ToString(CultureInfo.InvariantCulture)}",
                    GroupName = raw.threadGroupName ?? string.Empty,
                };
                if (matchMain && IsMainThread(candidate)
                    || matchRender && IsRenderThread(candidate)
                    || matchWorker
                    && TryParseJobWorkerIndex(candidate, out var workerIndex)
                    && workerIndex == requestedWorkerIndex)
                {
                    thread = candidate;
                    diagnostic = string.Empty;
                    return true;
                }
            }

            thread = default;
            diagnostic = hasThreads
                ? "Profiler thread was not found."
                : $"No profiler threads are available for frame {frameIndex}.";
            return false;
        }

        static List<int> GetAvailableFrames()
        {
            var frames = new List<int>();
            var first = ProfilerDriver.firstFrameIndex;
            var last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < 0 || first > last)
                return frames;

            var frame = first;
            while (frame >= 0 && frame <= last)
            {
                frames.Add(frame);
                if (frame == last)
                    break;

                var next = ProfilerDriver.GetNextFrameIndex(frame);
                if (next <= frame)
                    break;

                frame = next;
            }

            return frames;
        }

        static int CountAvailableFrames()
        {
            var first = ProfilerDriver.firstFrameIndex;
            var last = ProfilerDriver.lastFrameIndex;
            if (first < 0 || last < 0 || first > last)
                return 0;

            var count = 0;
            var frame = first;
            while (frame >= 0 && frame <= last)
            {
                count++;
                if (frame == last)
                    break;

                var next = ProfilerDriver.GetNextFrameIndex(frame);
                if (next <= frame)
                    break;

                frame = next;
            }

            return count;
        }

        static void SaveProfile(string path)
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            ProfilerDriver.SaveProfile(path);
        }

        static CapturePath ResolveCapturePath(string? fileName, bool allocateDefault)
        {
            var projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
            var value = fileName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!allocateDefault)
                    value = "capture.data";
                else
                    value = $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.data";
            }

            if (Path.GetExtension(value).Length == 0)
                value += ".data";

            if (!Path.IsPathRooted(value)
                && ContainsParentTraversal(value.AsSpan()))
                throw new InvalidOperationException(
                    $"Relative profiler capture path '{value}' contains parent traversal."
                );

            string absolutePath;
            if (Path.IsPathRooted(value))
                absolutePath = Path.GetFullPath(value);
            else if (value.IndexOf('/') < 0 && value.IndexOf('\\') < 0)
                absolutePath = Path.GetFullPath(Path.Combine(projectRoot, CaptureDirectory, value));
            else
                absolutePath = Path.GetFullPath(Path.Combine(projectRoot, value));

            return new()
            {
                AbsolutePath = absolutePath,
                DisplayPath = ToDisplayPath(absolutePath),
            };

            static bool ContainsParentTraversal(ReadOnlySpan<char> path)
            {
                var segmentStart = 0;
                for (var index = 0; index <= path.Length; index++)
                {
                    if (index < path.Length && path[index] is not ('/' or '\\'))
                        continue;

                    if (path[segmentStart..index].SequenceEqual("..".AsSpan()))
                        return true;

                    segmentStart = index + 1;
                }

                return false;
            }
        }

        static string ToDisplayPath(string absolutePath)
        {
            var projectRoot = Path.GetFullPath(ConduitAssetPathUtility.GetProjectRootPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(absolutePath);
            if (fullPath.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(projectRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return fullPath[(projectRoot.Length + 1)..].Replace('\\', '/');

            return fullPath.Replace('\\', '/');
        }

        static bool TryValidateTarget(string target, out string diagnostic)
        {
            if (target != "play_mode" && target != "edit_mode")
            {
                diagnostic = "Target must be play_mode or edit_mode.";
                return false;
            }

            if (target == "play_mode" && !EditorApplication.isPlaying)
            {
                diagnostic = "Unity is in edit mode. Enter play mode first or set target=\"edit_mode\".";
                return false;
            }

            if (target == "edit_mode" && EditorApplication.isPlaying)
            {
                diagnostic = "Unity is in play mode. Exit play mode first or set target=\"play_mode\".";
                return false;
            }

            if (EditorApplication.isPaused)
            {
                diagnostic = "Unity is paused. Resume the editor before capturing profiler frames.";
                return false;
            }

            diagnostic = string.Empty;
            return true;
        }

        static TimeSpan BuildCaptureTimeout(int frames)
            => TimeSpan.FromSeconds(Math.Min(120, Math.Max(10, frames / 5.0 + 10)));

        static Dictionary<string, string> ParseArgs(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in args ?? Array.Empty<string>())
            {
                var separatorIndex = arg.IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex < 0)
                    continue;

                options[arg[..separatorIndex]] = arg[(separatorIndex + 1)..];
            }

            return options;
        }

        static string GetOption(Dictionary<string, string> options, string key, string defaultValue)
            => options.TryGetValue(key, out var value) && value is { Length: > 0 } ? value : defaultValue;

        static int ParseInt(string value, int defaultValue)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;

        static double ParseDouble(string value, double defaultValue)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;

        static bool ParseBool(string value, bool defaultValue)
            => bool.TryParse(value, out var parsed) ? parsed : defaultValue;

        static int Clamp(int value, int min, int max)
            => Math.Min(max, Math.Max(min, value));

        static double Clamp(double value, double min, double max)
            => Math.Min(max, Math.Max(min, value));

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
                AppendPadded(builder, FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex), 9);
                AppendPadded(builder, FormatNumber(row.CpuMs), 10);
                AppendPadded(builder, FormatOptionalNumber(row.GpuMs), 10);
                AppendPadded(builder, FormatNumber(row.Fps), 10);
                AppendPadded(builder, FormatKb(row.GcBytes), 9);
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

                AppendPadded(builder, FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex), 7);
                AppendPadded(builder, FormatNumber(row.TotalMs), 10);
                AppendPadded(builder, FormatNumber(row.SelfMs), 9);
                AppendPadded(builder, FormatKb(row.GcBytes), 7);
                AppendPadded(builder, (int)Math.Round(row.Calls), 7);
                AppendPadded(builder, FormatPercent(row.TotalMs, row.FrameTimeMs), 9);
                builder.AppendLine(includeRank ? FormatSamplePath(row.DisplayPath) : FormatSampleName(row.Name));
            }
        }

        static void AppendBrowseRow(StringBuilder builder, HierarchyRow row, int rootDepth, float frameTimeMs, int aggregateWorkerCount)
        {
            var relativeDepth = row.Depth - rootDepth;
            var normalizedMs = aggregateWorkerCount > 0 ? GetNormalizedWorkerMs(row, aggregateWorkerCount) : row.TotalMs;
            AppendPadded(builder, row.PublicId ?? "", 10);
            AppendPadded(builder, relativeDepth, 7);
            AppendPadded(builder, FormatNumber(row.TotalMs), 10);
            if (aggregateWorkerCount > 0)
            {
                AppendPadded(builder, FormatNumber(GetWorkerMeanMs(row)), 9);
                AppendPadded(builder, FormatNumber(row.MinTotalMs), 8);
                AppendPadded(builder, FormatNumber(row.MaxTotalMs), 8);
                AppendPadded(builder, row.ContributingWorkerCount, 9);
            }

            AppendPadded(builder, FormatNumber(row.SelfMs), 9);
            AppendPadded(builder, FormatKb(row.GcBytes), 7);
            AppendPadded(builder, (int)Math.Round(row.Calls), 7);
            AppendPadded(builder, FormatPercent(normalizedMs, frameTimeMs), 9);
            builder.Append(' ', Math.Max(0, relativeDepth * 2));
            builder.AppendLine(FormatSampleName(row.Name));
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

        static void Flatten(HierarchyRow root, List<HierarchyRow> rows)
        {
            using var pooledPending = ConduitUtility.GetPooledList<HierarchyRow>(out var pending);
            pending.Add(root);
            while (pending.Count > 0)
            {
                var lastIndex = pending.Count - 1;
                var row = pending[lastIndex];
                pending.RemoveAt(lastIndex);
                rows.Add(row);
                for (var index = row.Children.Count - 1; index >= 0; --index)
                    pending.Add(row.Children[index]);
            }
        }

        static string NormalizeIdentitySegment(string name)
            => name.Replace('/', '∕').Trim();

        static string BuildDisplayPath(HierarchyRow row)
        {
            using var pooledAncestors = ConduitUtility.GetPooledList<HierarchyRow>(out var ancestors);
            for (var current = row; current != null; current = current.Parent)
                ancestors.Add(current);

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            for (var index = ancestors.Count - 1; index >= 0; --index)
            {
                if (index < ancestors.Count - 1)
                    builder.Append('/');

                builder.Append(ancestors[index].Name);
            }

            return builder.ToString();
        }

        static uint AppendIdentitySegment(
            uint hash,
            string segment,
            int occurrence,
            bool includeSeparator)
        {
            unchecked
            {
                if (includeSeparator)
                    hash = AppendStableHash(hash, '/');

                hash = AppendStableHash(hash, segment);
                hash = AppendStableHash(hash, '[');
                hash = AppendPositiveIntegerHash(hash, occurrence);
                return AppendStableHash(hash, ']');
            }
        }

        static uint AppendPositiveIntegerHash(uint hash, int value)
        {
            Span<char> digits = stackalloc char[10];
            var offset = digits.Length;
            do
            {
                digits[--offset] = (char)('0' + value % 10);
                value /= 10;
            }
            while (value > 0);

            foreach (var digit in digits[offset..])
                hash = AppendStableHash(hash, digit);

            return hash;
        }

        static uint StableHash(string value)
            => AppendStableHash(2166136261, value);

        static uint AppendStableHash(uint hash, string value)
        {
            foreach (var character in value)
                hash = AppendStableHash(hash, character);

            return hash;
        }

        static uint AppendStableHash(uint hash, char value)
        {
            unchecked
            {
                hash ^= value;
                return hash * 16777619;
            }
        }

        static string StableId(string value) => StableId(StableHash(value));

        static string StableId(uint hash) => hash.ToString("x8", CultureInfo.InvariantCulture);

        static string? ClassifyThread(string? threadName, string? threadGroupName)
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

        static bool IsMainThread(ThreadInfo info)
            => string.Equals(info.Name.Trim(), "Main Thread", StringComparison.OrdinalIgnoreCase)
               || string.Equals(info.Name.Trim(), "UnityMain", StringComparison.OrdinalIgnoreCase);

        static bool IsRenderThread(ThreadInfo info)
            => string.Equals(info.Name.Trim(), "Render Thread", StringComparison.OrdinalIgnoreCase);

        static bool TryParseJobWorkerIndex(ThreadInfo info, out int workerIndex)
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

        static string FormatWorkerLabel(int workerIndex)
        {
            if ((uint)workerIndex < jobWorkerLabels.Length)
                return jobWorkerLabels[workerIndex] ??=
                    $"worker{workerIndex.ToString(CultureInfo.InvariantCulture)}";

            return $"worker{workerIndex.ToString(CultureInfo.InvariantCulture)}";
        }

        static string FormatThreadLabels(IEnumerable<string> labels)
        {
            using var pooledSorted = ConduitUtility.GetPooledList<string>(out var sorted);
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

        static string FormatFrameOrdinal(int frameOrdinal, int frameIndex)
            => (frameOrdinal >= 0 ? frameOrdinal : frameIndex).ToString(CultureInfo.InvariantCulture);

        static string FormatSamplePath(string displayPath)
        {
            if (string.IsNullOrWhiteSpace(displayPath))
                return "<unnamed>";

            var segments = displayPath.Split('/');
            var firstDetailedSegmentIndex = Math.Max(0, segments.Length - 3);
            for (var i = 0; i < segments.Length; i++)
                segments[i] = FormatSampleSegment(segments[i], keepNamespace: i >= firstDetailedSegmentIndex);

            return string.Join("/", segments);
        }

        static string FormatSampleName(string name) => FormatSampleSegment(name, keepNamespace: true);

        static string FormatSampleSegment(string segment, bool keepNamespace)
        {
            var value = StripProfilerAssemblyPrefix(segment).Replace("()", string.Empty).Trim();
            if (!keepNamespace)
                value = StripNamespace(value);

            return value.Length == 0 ? "<unnamed>" : value;
        }

        /*
         * Unity profiler markers frequently include assembly-qualified method names.
         * Overview paths stay readable by keeping exact qualification only near the leaf.
         */
        static string StripProfilerAssemblyPrefix(string value)
        {
            var separatorIndex = value.IndexOf('!');
            return separatorIndex < 0 ? value : value[(separatorIndex + 1)..];
        }

        static string StripNamespace(string value)
        {
            var namespaceSeparatorIndex = value.IndexOf("::", StringComparison.Ordinal);
            if (namespaceSeparatorIndex >= 0)
                return value[(namespaceSeparatorIndex + 2)..].Trim();

            var lastDotIndex = value.LastIndexOf('.');
            if (lastDotIndex < 0)
                return value;

            var typeSeparatorIndex = value.LastIndexOf('.', lastDotIndex - 1);
            return typeSeparatorIndex < 0 ? value : value[(typeSeparatorIndex + 1)..].Trim();
        }

        static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        static string FormatOptionalNumber(double value) => value <= 0 ? "n/a" : FormatNumber(value);

        static string FormatKb(double bytes)
        {
            var kb = bytes / 1024.0;
            return bytes > 0 && kb < 0.1 ? "<0.1" : kb.ToString("0.#", CultureInfo.InvariantCulture);
        }

        static string FormatMb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);

        static string FormatPercent(double ms, double frameMs)
            => frameMs <= 0 ? "0" : (ms / frameMs * 100.0).ToString("0.#", CultureInfo.InvariantCulture);

        static BridgeCommandResult Success(string text)
            => new()
            {
                outcome = ToolOutcome.Success,
                return_value = text,
            };

        static BridgeCommandResult Failure(string title, string error, string? file)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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
                return_value = builder.TrimEnd().ToString(),
            };
        }

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
            start = Clamp(start, 0, frames.Count - 1);
            end = Clamp(end, 0, frames.Count - 1);
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

        internal static string BuildStableIdForTest(string value) => StableId(value);

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

        internal static bool TryGetOverviewThreadLabelForTest(string? threadName, string? threadGroupName, out string label)
        {
            label = ClassifyThread(threadName, threadGroupName) ?? string.Empty;
            return label.Length > 0;
        }

        internal static string FormatThreadLabelsForTest(IEnumerable<string> labels)
            => FormatThreadLabels(labels);

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

        internal static string FormatSamplePathForTest(string displayPath) => FormatSamplePath(displayPath);

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

        internal struct CapturePath
        {
            public string AbsolutePath;
            public string DisplayPath;
        }

        struct ThreadInfo
        {
            public int Index;
            public string Name;
            public string GroupName;
        }

        struct FrameStats
        {
            public int FrameIndex;
            public int FrameOrdinal;
            public double CpuMs;
            public double GpuMs;
            public double Fps;
            public long SampleCount;
            public double GcBytes;
        }

        struct SampleRow
        {
            public int FrameIndex;
            public int FrameOrdinal;
            public string Name;
            public string DisplayPath;
            public int Depth;
            public int ChildCount;
            public double TotalMs;
            public double SelfMs;
            public double GcBytes;
            public double Calls;
            public float FrameTimeMs;
        }

        internal sealed class HierarchyRow
        {
            public int ItemId;
            public HierarchyRow? Parent;
            public string? PublicId;
            public string Name = string.Empty;
            public uint IdentityHash;
            public int Depth;
            public double TotalMs;
            public double SelfMs;
            public double GcBytes;
            public double Calls;
            public int ContributingWorkerCount;
            public double MinTotalMs;
            public double MaxTotalMs;
            public List<HierarchyRow> Children { get; } = new();
            internal Dictionary<string, HierarchyRow>? MergedChildren;
        }
    }
}
