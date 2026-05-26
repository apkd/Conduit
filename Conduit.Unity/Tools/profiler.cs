#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
        static readonly Regex jobWorkerThreadNamePattern = new(
            @"^(?:Worker|Job Worker) (?<index>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        public static string BuildStatusLine()
        {
            try
            {
                if (!ProfilerDriver.enabled)
                    return "Profiler: not recording";

                var frames = GetAvailableFrames();
                var selected = TryGetSelectedFrame(frames, out var selectedFrame)
                    ? selectedFrame.ToString(CultureInfo.InvariantCulture)
                    : "none";

                if (frames.Count == 0)
                    return $"Profiler: recording; frames=none; selected={selected}; latest=none; total_allocated={FormatMb(Profiler.GetTotalAllocatedMemoryLong())}MB; gc_reserved={FormatMb(Profiler.GetMonoHeapSizeLong())}MB; system_used={FormatMb(Profiler.GetTotalReservedMemoryLong())}MB";

                var latest = frames[^1];
                var stats = ReadFrameStats(latest);
                return $"Profiler: recording; frames={frames[0]}..{frames[^1]}; selected={selected}; latest={latest}; cpu={FormatNumber(stats.CpuMs)}ms; gpu={FormatOptionalNumber(stats.GpuMs)}ms; fps={FormatNumber(stats.Fps)}; total_allocated={FormatMb(Profiler.GetTotalAllocatedMemoryLong())}MB; gc_reserved={FormatMb(Profiler.GetMonoHeapSizeLong())}MB; system_used={FormatMb(Profiler.GetTotalReservedMemoryLong())}MB";
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
                var frames = ResolveFrameRange(frameRange, out var warnings);
                if (frames.Count == 0)
                    return Success("No profiler frames available. Use profiler_record action=capture first.");

                var availableFrames = GetAvailableFrames();
                var ordinalByFrame = BuildFrameOrdinalMap(availableFrames);
                var frameStats = new List<FrameStats>(frames.Count);
                var threadLabels = new HashSet<string>(StringComparer.Ordinal);
                long sampleCount = 0;
                foreach (var frame in frames)
                {
                    var stats = ReadFrameStats(frame, ordinalByFrame.GetValueOrDefault(frame, -1));
                    frameStats.Add(stats);
                    sampleCount += stats.SampleCount;
                    foreach (var thread in stats.Threads)
                        threadLabels.Add(thread);
                }

                var builder = new StringBuilder();
                builder.Append("Threads: ");
                builder.AppendLine(FormatThreadLabels(threadLabels));
                builder.Append("Sample count: ");
                builder.AppendLine(sampleCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine();

                if (mode == "gc_kb")
                    AppendGcOverview(builder, frameStats, warnings);
                else
                    AppendCpuOverview(builder, frameStats, warnings);

                return Success(builder.ToString().TrimEnd());
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

                if (!TryResolveThread(frame, threadSelector, out var thread, out var threadDiagnostic))
                    return Failure("Unable to browse profiler hierarchy.", threadDiagnostic, null);

                using var hierarchy = ProfilerDriver.GetHierarchyFrameDataView(
                    frame,
                    thread.Index,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnDontSort,
                    false
                );

                if (!hierarchy.valid)
                    return Failure("Unable to browse profiler hierarchy.", $"No hierarchy data is available for frame {frame} thread {thread.Index}.", null);

                var root = BuildHierarchy(hierarchy, sort);
                AssignPublicIds(root);
                var selectedRoot = ResolveRoot(root, rootSelector, warnings);
                if (selectedRoot == null)
                    return Failure("Unable to browse profiler hierarchy.", $"Root '{rootSelector}' was not found.", null);

                var visibleRows = SelectVisibleRows(selectedRoot, depth, sort, onlyNonTrivial, hierarchy.frameTimeMs);
                var builder = new StringBuilder();
                builder.AppendLine("id      depth  total_ms  self_ms  gc_kb  calls  frame_%  name");

                var printed = 0;
                foreach (var row in EnumerateForOutput(selectedRoot, visibleRows, sort))
                {
                    if (printed >= limit)
                    {
                        warnings.Add("row_limit_reached");
                        break;
                    }

                    AppendBrowseRow(builder, row, selectedRoot.Depth, hierarchy.frameTimeMs);
                    printed++;
                }

                AppendWarnings(builder, warnings);
                return Success(builder.ToString().TrimEnd());
            }
            catch (Exception exception)
            {
                return Failure("Unable to browse profiler hierarchy.", exception.Message, null);
            }
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
            try
            {
                ProfilerDriver.enabled = false;
                ProfilerDriver.profileEditor = target == "edit_mode";
                ProfilerDriver.ClearAllFrames();
                ProfilerDriver.enabled = true;

                var deadlineUtc = DateTime.UtcNow + BuildCaptureTimeout(frames);
                while (CountAvailableFrames() < frames && DateTime.UtcNow < deadlineUtc)
                    await Task.Delay(50);
            }
            finally
            {
                ProfilerDriver.enabled = false;
                ProfilerDriver.profileEditor = previousProfileEditor;
            }

            var capturedFrames = CountAvailableFrames();
            if (capturedFrames < frames)
                return Failure("Unable to capture profile.", $"Captured {capturedFrames} of {frames} requested frames before the internal capture deadline.", null);

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var path = ResolveCapturePath(fileName, allocateDefault: false);
                SaveProfile(path.AbsolutePath);
                return Success(
                    $"Profile captured and saved!\nFrame count: {capturedFrames.ToString(CultureInfo.InvariantCulture)}\nFile: {path.DisplayPath}"
                );
            }

            return Success($"Profile captured!\nFrame count: {capturedFrames.ToString(CultureInfo.InvariantCulture)}");
        }

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

            var builder = new StringBuilder();
            builder.AppendLine("Profile captures:");
            foreach (var file in files)
                builder.AppendLine(ToDisplayPath(file));

            return Success(builder.ToString().TrimEnd());
        }

        static void AppendCpuOverview(StringBuilder builder, List<FrameStats> frameStats, List<string> warnings)
        {
            builder.AppendLine("Worst frames, sorted by cpu_ms:");
            AppendFrameTable(builder, TopFrames(frameStats, stats => stats.CpuMs));
            builder.AppendLine();

            var samples = CollectMainThreadSamples(frameStats);
            builder.AppendLine("Interesting samples, sorted by actionable_cpu_ms:");
            AppendSampleTable(builder, TopInterestingSamples(samples, "cpu_ms"), includeRank: true);
            AppendWarnings(builder, warnings);
        }

        static void AppendGcOverview(StringBuilder builder, List<FrameStats> frameStats, List<string> warnings)
        {
            builder.AppendLine("Worst frames, sorted by gc_kb:");
            AppendFrameTable(builder, TopFrames(frameStats, stats => stats.GcBytes));
            builder.AppendLine();

            builder.AppendLine("Interesting samples, sorted by gc_kb:");
            AppendSampleTable(builder, TopInterestingSamples(CollectMainThreadSamples(frameStats), "gc_kb"), includeRank: true);
            AppendWarnings(builder, warnings);
        }

        static List<SampleRow> CollectMainThreadSamples(List<FrameStats> frames)
        {
            var samples = new List<SampleRow>();
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

                CollectSampleChildren(hierarchy, hierarchy.GetRootItemID(), frame.FrameIndex, frame.FrameOrdinal, hierarchy.frameTimeMs, samples, identityPath: "", displayPath: "", depth: 0);
            }

            return samples;
        }

        static void CollectSampleChildren(
            HierarchyFrameDataView hierarchy,
            int itemId,
            int frameIndex,
            int frameOrdinal,
            float frameTimeMs,
            List<SampleRow> samples,
            string identityPath,
            string displayPath,
            int depth
        )
        {
            using var pooledChildren = ConduitUtility.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                var childName = hierarchy.GetItemName(child) ?? "<unnamed>";
                var segment = NormalizeIdentitySegment(childName);
                occurrences.TryGetValue(segment, out var occurrence);
                occurrences[segment] = ++occurrence;

                var childIdentityPath = string.IsNullOrEmpty(identityPath)
                    ? $"{segment}[{occurrence.ToString(CultureInfo.InvariantCulture)}]"
                    : $"{identityPath}/{segment}[{occurrence.ToString(CultureInfo.InvariantCulture)}]";
                var childDisplayPath = string.IsNullOrEmpty(displayPath) ? childName : $"{displayPath}/{childName}";
                CollectSampleTree(hierarchy, child, frameIndex, frameOrdinal, frameTimeMs, samples, childIdentityPath, childDisplayPath, depth);
            }
        }

        static void CollectSampleTree(
            HierarchyFrameDataView hierarchy,
            int itemId,
            int frameIndex,
            int frameOrdinal,
            float frameTimeMs,
            List<SampleRow> samples,
            string identityPath,
            string displayPath,
            int depth
        )
        {
            using var pooledChildren = ConduitUtility.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            samples.Add(ReadSampleRow(hierarchy, itemId, frameIndex, frameOrdinal, frameTimeMs, identityPath, displayPath, depth, children.Count));

            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in children)
            {
                var childName = hierarchy.GetItemName(child) ?? "<unnamed>";
                var segment = NormalizeIdentitySegment(childName);
                occurrences.TryGetValue(segment, out var occurrence);
                occurrences[segment] = ++occurrence;

                CollectSampleTree(
                    hierarchy,
                    child,
                    frameIndex,
                    frameOrdinal,
                    frameTimeMs,
                    samples,
                    $"{identityPath}/{segment}[{occurrence.ToString(CultureInfo.InvariantCulture)}]",
                    $"{displayPath}/{childName}",
                    depth + 1
                );
            }
        }

        static FrameStats ReadFrameStats(int frameIndex, int frameOrdinal = -1)
        {
            var stats = new FrameStats { FrameIndex = frameIndex, FrameOrdinal = frameOrdinal };
            for (var threadIndex = 0; ; threadIndex++)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex);
                if (!raw.valid)
                    break;

                if (stats.ThreadCount == 0)
                {
                    stats.CpuMs = raw.frameTimeMs;
                    stats.GpuMs = raw.frameGpuTimeMs;
                    stats.Fps = raw.frameFps;
                }

                stats.ThreadCount++;
                stats.SampleCount += raw.sampleCount;
                stats.GcBytes += ReadGcAllocBytes(raw);
                if (ClassifyThread(raw.threadName, raw.threadGroupName) is { } label)
                    stats.Threads.Add(label);
            }

            return stats;
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

        static HierarchyRow BuildHierarchy(HierarchyFrameDataView hierarchy, string sort)
            => BuildHierarchyRow(hierarchy, hierarchy.GetRootItemID(), parent: null, depth: 0, identityPath: "", displayPath: "", sort);

        static HierarchyRow BuildHierarchyRow(
            HierarchyFrameDataView hierarchy,
            int itemId,
            HierarchyRow? parent,
            int depth,
            string identityPath,
            string displayPath,
            string sort
        )
        {
            var name = hierarchy.GetItemName(itemId) ?? "<unnamed>";
            var currentDisplayPath = string.IsNullOrEmpty(displayPath) ? name : $"{displayPath}/{name}";
            var row = new HierarchyRow
            {
                ItemId = itemId,
                Parent = parent,
                Name = name,
                Depth = depth,
                IdentityPath = string.IsNullOrEmpty(identityPath) ? $"{NormalizeIdentitySegment(name)}[1]" : identityPath,
                DisplayPath = currentDisplayPath,
                TotalMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnTotalTime),
                SelfMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnSelfTime),
                GcBytes = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnGcMemory),
                Calls = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnCalls),
            };

            using var pooledChildren = ConduitUtility.GetPooledList<int>(out var children);
            hierarchy.GetItemChildren(itemId, children);
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
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
                        $"{row.IdentityPath}/{segment}[{occurrence.ToString(CultureInfo.InvariantCulture)}]",
                        currentDisplayPath,
                        sort
                    )
                );
            }

            row.Children.Sort((left, right) => CompareRows(left, right, sort));
            return row;
        }

        static void AssignPublicIds(HierarchyRow root)
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in Flatten(root))
            {
                var baseId = StableId(row.IdentityPath);
                used.TryGetValue(baseId, out var count);
                used[baseId] = ++count;
                row.PublicId = count == 1 ? baseId : $"{baseId}-{count.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        static HierarchyRow? ResolveRoot(HierarchyRow root, string selector, List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(selector))
                return root;

            var rows = Flatten(root);
            foreach (var row in rows)
                if (string.Equals(row.PublicId, selector, StringComparison.Ordinal))
                    return row;

            foreach (var row in rows)
                if (string.Equals(row.DisplayPath, selector, StringComparison.OrdinalIgnoreCase))
                    return row;

            var exactMatches = new List<HierarchyRow>();
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

        static HashSet<HierarchyRow> SelectVisibleRows(HierarchyRow root, int depth, string sort, bool onlyNonTrivial, float frameTimeMs)
        {
            var visibleRows = new HashSet<HierarchyRow>();
            Mark(root, 0);
            return visibleRows;

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

        static IEnumerable<HierarchyRow> EnumerateForOutput(HierarchyRow root, HashSet<HierarchyRow> visibleRows, string sort)
        {
            if (!visibleRows.Contains(root))
                yield break;

            yield return root;
            foreach (var row in EnumerateChildren(root))
                yield return row;

            IEnumerable<HierarchyRow> EnumerateChildren(HierarchyRow parent)
            {
                parent.Children.Sort((left, right) => CompareRows(left, right, sort));
                foreach (var child in parent.Children)
                {
                    if (!visibleRows.Contains(child))
                        continue;

                    yield return child;
                    foreach (var descendant in EnumerateChildren(child))
                        yield return descendant;
                }
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

        static Dictionary<int, int> BuildFrameOrdinalMap(List<int> frames)
        {
            var ordinals = new Dictionary<int, int>();
            for (var i = 0; i < frames.Count; i++)
                ordinals[frames[i]] = i;

            return ordinals;
        }

        static List<int> ResolveFrameRange(string frameRange, out List<string> warnings)
        {
            warnings = new();
            var frames = GetAvailableFrames();
            if (frames.Count == 0)
                return frames;

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

        static int ResolveFrameEndpoint(string endpoint, int frameCount)
        {
            endpoint = endpoint.Trim();
            if (endpoint.StartsWith("^", StringComparison.Ordinal))
            {
                var distance = ParseInt(endpoint[1..], 1);
                return frameCount - distance;
            }

            return ParseInt(endpoint, 0);
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

        static bool TryResolveThread(int frameIndex, string selector, out ThreadInfo thread, out string diagnostic)
        {
            var threads = ListThreads(frameIndex);
            if (threads.Count == 0)
            {
                thread = default;
                diagnostic = $"No profiler threads are available for frame {frameIndex}.";
                return false;
            }

            if (string.Equals(selector, "main", StringComparison.OrdinalIgnoreCase))
                return TryFindThread(threads, IsMainThread, out thread, out diagnostic);

            if (string.Equals(selector, "render", StringComparison.OrdinalIgnoreCase))
                return TryFindThread(threads, IsRenderThread, out thread, out diagnostic);

            if (selector.StartsWith("worker", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(selector["worker".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var workerIndex))
                return TryFindThread(threads, info => TryParseJobWorkerIndex(info, out var index) && index == workerIndex, out thread, out diagnostic);

            diagnostic = $"Profiler thread '{selector}' was not found. Use main, render, or worker<N>.";
            thread = default;
            return false;
        }

        static bool TryFindThread(List<ThreadInfo> threads, Predicate<ThreadInfo> predicate, out ThreadInfo thread, out string diagnostic)
        {
            foreach (var candidate in threads)
                if (predicate(candidate))
                    return UseThread(candidate, out thread, out diagnostic);

            diagnostic = "Profiler thread was not found.";
            thread = default;
            return false;
        }

        static bool UseThread(ThreadInfo selectedThread, out ThreadInfo thread, out string diagnostic)
        {
            thread = selectedThread;
            diagnostic = string.Empty;
            return true;
        }

        static List<ThreadInfo> ListThreads(int frameIndex)
        {
            var threads = new List<ThreadInfo>();
            for (var threadIndex = 0; ; threadIndex++)
            {
                using var raw = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex);
                if (!raw.valid)
                    break;

                threads.Add(new()
                {
                    Index = threadIndex,
                    Name = raw.threadName ?? $"Thread {threadIndex.ToString(CultureInfo.InvariantCulture)}",
                    GroupName = raw.threadGroupName ?? string.Empty,
                });
            }

            return threads;
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

        static int CountAvailableFrames() => GetAvailableFrames().Count;

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

            string absolutePath;
            if (Path.IsPathRooted(value))
                absolutePath = Path.GetFullPath(value);
            else if (value.IndexOfAny(new[] { '/', '\\' }) < 0)
                absolutePath = Path.GetFullPath(Path.Combine(projectRoot, CaptureDirectory, value));
            else
                absolutePath = Path.GetFullPath(Path.Combine(projectRoot, value));

            return new()
            {
                AbsolutePath = absolutePath,
                DisplayPath = ToDisplayPath(absolutePath),
            };
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
            string identityPath,
            string displayPath,
            int depth,
            int childCount
        )
            => new()
            {
                FrameIndex = frameIndex,
                FrameOrdinal = frameOrdinal,
                Name = hierarchy.GetItemName(itemId) ?? "<unnamed>",
                IdentityPath = identityPath,
                DisplayPath = displayPath,
                Depth = depth,
                ChildCount = childCount,
                TotalMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnTotalTime),
                SelfMs = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnSelfTime),
                GcBytes = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnGcMemory),
                Calls = ReadColumn(hierarchy, itemId, HierarchyFrameDataView.columnCalls),
                FrameTimeMs = frameTimeMs,
            };

        static IEnumerable<FrameStats> TopFrames(List<FrameStats> frames, Func<FrameStats, double> selector)
        {
            frames.Sort((left, right) => selector(right).CompareTo(selector(left)));
            return frames.GetRange(0, Math.Min(OverviewRowLimit, frames.Count));
        }

        static IEnumerable<SampleRow> TopSamples(List<SampleRow> samples, Func<SampleRow, double> selector)
        {
            samples.Sort((left, right) => selector(right).CompareTo(selector(left)));
            return samples.GetRange(0, Math.Min(OverviewRowLimit, samples.Count));
        }

        static IEnumerable<SampleRow> TopInterestingSamples(List<SampleRow> samples, string mode)
        {
            var bestByPath = new Dictionary<string, SampleRow>(StringComparer.Ordinal);
            foreach (var sample in samples)
            {
                if (!ShouldIncludeOverviewSample(sample, mode))
                    continue;

                var key = string.IsNullOrEmpty(sample.IdentityPath) ? sample.DisplayPath : sample.IdentityPath;
                if (!bestByPath.TryGetValue(key, out var existing) || CompareOverviewSamples(sample, existing, mode) < 0)
                    bestByPath[key] = sample;
            }

            var rows = new List<SampleRow>(bestByPath.Values);
            rows.Sort((left, right) => CompareOverviewSamples(left, right, mode));
            return rows.GetRange(0, Math.Min(OverviewRowLimit, rows.Count));
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

        static void AppendFrameTable(StringBuilder builder, IEnumerable<FrameStats> rows)
        {
            builder.AppendLine("frame    cpu_ms    gpu_ms    fps       gc_kb    samples");
            foreach (var row in rows)
            {
                builder.Append(FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex).PadRight(9));
                builder.Append(FormatNumber(row.CpuMs).PadRight(10));
                builder.Append(FormatOptionalNumber(row.GpuMs).PadRight(10));
                builder.Append(FormatNumber(row.Fps).PadRight(10));
                builder.Append(FormatKb(row.GcBytes).PadRight(9));
                builder.AppendLine(row.SampleCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        static void AppendSampleTable(StringBuilder builder, IEnumerable<SampleRow> rows, bool includeRank = false)
        {
            builder.AppendLine(includeRank
                ? "rank  frame  total_ms  self_ms  gc_kb  calls  frame_%  sample"
                : "frame  total_ms  self_ms  gc_kb  calls  frame_%  name");
            var rank = 1;
            foreach (var row in rows)
            {
                if (includeRank)
                    builder.Append((rank++).ToString(CultureInfo.InvariantCulture).PadRight(6));

                builder.Append(FormatFrameOrdinal(row.FrameOrdinal, row.FrameIndex).PadRight(7));
                builder.Append(FormatNumber(row.TotalMs).PadRight(10));
                builder.Append(FormatNumber(row.SelfMs).PadRight(9));
                builder.Append(FormatKb(row.GcBytes).PadRight(7));
                builder.Append(((int)Math.Round(row.Calls)).ToString(CultureInfo.InvariantCulture).PadRight(7));
                builder.Append(FormatPercent(row.TotalMs, row.FrameTimeMs).PadRight(9));
                builder.AppendLine(includeRank ? FormatSamplePath(row.DisplayPath) : row.Name);
            }
        }

        static void AppendBrowseRow(StringBuilder builder, HierarchyRow row, int rootDepth, float frameTimeMs)
        {
            var relativeDepth = row.Depth - rootDepth;
            builder.Append((row.PublicId ?? "").PadRight(8));
            builder.Append(relativeDepth.ToString(CultureInfo.InvariantCulture).PadRight(7));
            builder.Append(FormatNumber(row.TotalMs).PadRight(10));
            builder.Append(FormatNumber(row.SelfMs).PadRight(9));
            builder.Append(FormatKb(row.GcBytes).PadRight(7));
            builder.Append(((int)Math.Round(row.Calls)).ToString(CultureInfo.InvariantCulture).PadRight(7));
            builder.Append(FormatPercent(row.TotalMs, frameTimeMs).PadRight(9));
            builder.Append(new string(' ', Math.Max(0, relativeDepth * 2)));
            builder.AppendLine(row.Name);
        }

        static void AppendWarnings(StringBuilder builder, List<string> warnings)
        {
            if (warnings.Count == 0)
                return;

            builder.AppendLine();
            builder.Append("warnings: ");
            builder.AppendLine(string.Join(", ", warnings));
        }

        static int CompareRows(HierarchyRow left, HierarchyRow right, string sort)
        {
            var result = sort switch
            {
                "self_ms"  => right.SelfMs.CompareTo(left.SelfMs),
                "gc_bytes" => right.GcBytes.CompareTo(left.GcBytes),
                "calls"    => right.Calls.CompareTo(left.Calls),
                _          => right.TotalMs.CompareTo(left.TotalMs),
            };

            return result != 0 ? result : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        }

        static IEnumerable<HierarchyRow> Flatten(HierarchyRow root)
        {
            yield return root;
            foreach (var child in root.Children)
            foreach (var row in Flatten(child))
                yield return row;
        }

        static string NormalizeIdentitySegment(string name)
            => name.Replace('/', '∕').Trim();

        static string StableId(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= 16777619;
                }

                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        static string? ClassifyThread(string? threadName, string? threadGroupName)
        {
            var name = (threadName ?? string.Empty).Trim();
            if (string.Equals(name, "Main Thread", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "UnityMain", StringComparison.OrdinalIgnoreCase))
                return "main";

            if (string.Equals(name, "Render Thread", StringComparison.OrdinalIgnoreCase))
                return "render";

            if (TryParseJobWorkerIndex(threadName, threadGroupName, out var workerIndex))
                return $"worker{workerIndex.ToString(CultureInfo.InvariantCulture)}";

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

            var match = jobWorkerThreadNamePattern.Match((threadName ?? string.Empty).Trim());
            return match.Success
                   && int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out workerIndex);
        }

        static string FormatThreadLabels(IEnumerable<string> labels)
        {
            var sorted = new List<string>(labels);
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
            => string.IsNullOrWhiteSpace(displayPath) ? "<unnamed>" : displayPath;

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
            var builder = new StringBuilder();
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
                return_value = builder.ToString().TrimEnd(),
            };
        }

        internal static CapturePath ResolveCapturePathForTest(string? fileName, bool allocateDefault)
            => ResolveCapturePath(fileName, allocateDefault);

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

        sealed class FrameStats
        {
            public int FrameIndex;
            public int FrameOrdinal;
            public double CpuMs;
            public double GpuMs;
            public double Fps;
            public long SampleCount;
            public double GcBytes;
            public int ThreadCount;
            public List<string> Threads { get; } = new();
        }

        sealed class SampleRow
        {
            public int FrameIndex;
            public int FrameOrdinal;
            public string Name = string.Empty;
            public string IdentityPath = string.Empty;
            public string DisplayPath = string.Empty;
            public int Depth;
            public int ChildCount;
            public double TotalMs;
            public double SelfMs;
            public double GcBytes;
            public double Calls;
            public float FrameTimeMs;
        }

        sealed class HierarchyRow
        {
            public int ItemId;
            public HierarchyRow? Parent;
            public string? PublicId;
            public string Name = string.Empty;
            public string IdentityPath = string.Empty;
            public string DisplayPath = string.Empty;
            public int Depth;
            public double TotalMs;
            public double SelfMs;
            public double GcBytes;
            public double Calls;
            public List<HierarchyRow> Children { get; } = new();
        }
    }
}
