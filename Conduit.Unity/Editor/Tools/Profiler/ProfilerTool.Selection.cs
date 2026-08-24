#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Conduit
{
    static partial class ProfilerTool
    {
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

        static bool TryResolveThread(
            int frameIndex,
            string selector,
            out ProfilerThreadInfo thread,
            out string diagnostic)
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
                var candidate = new ProfilerThreadInfo(
                    threadIndex,
                    raw.threadName ?? $"Thread {threadIndex.ToString(CultureInfo.InvariantCulture)}",
                    raw.threadGroupName ?? string.Empty
                );
                if (matchMain && ProfilerThreadLabels.IsMainThread(candidate)
                    || matchRender && ProfilerThreadLabels.IsRenderThread(candidate)
                    || matchWorker
                    && ProfilerThreadLabels.TryParseJobWorkerIndex(candidate, out var workerIndex)
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
    }
}
