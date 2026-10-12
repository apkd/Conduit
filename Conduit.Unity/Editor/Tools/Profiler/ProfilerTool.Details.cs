#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Profiling;
using Unity.Profiling.LowLevel;

namespace Conduit
{
    static partial class ProfilerTool
    {
        sealed class SubmissionGroup
        {
            internal SampleCall Source = null!;
            internal SampleCall Longest = null!;
            internal int Calls;
            internal double CpuMs;
        }

        static void AppendSelectedDetails(StringBuilder builder, int frame, string thread, HierarchyRow row, int limit)
        {
            using var reader = new SampleReader(frame);
            var calls = reader.Calls(row);
            builder.AppendLine();
            builder.AppendLine($"Calls: {calls.Count}; inspect: {MakeReference(frame, thread, 'c', row.PublicId!)}");
            if (calls.Count == 0)
                return;
            var longest = calls[0];
            foreach (var call in calls)
                if (call.DurationMs > longest.DurationMs)
                    longest = call;
            builder.AppendLine($"Longest invocation: {ProfilerValueFormatter.FormatNumber(longest.DurationMs)} ms; {reader.Reference(longest)}");

            var groups = new Dictionary<string, SubmissionGroup>(StringComparer.Ordinal);
            var coverage = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                var source = reader.Source(call, out var status);
                coverage.TryGetValue(status, out var count);
                coverage[status] = count + 1;
                if (source == null)
                    continue;
                if (!groups.TryGetValue(source.SourceKey, out var group))
                    groups.Add(source.SourceKey, group = new() { Source = source, Longest = call });
                group.Calls++;
                group.CpuMs += call.DurationMs;
                if (call.DurationMs > group.Longest.DurationMs)
                {
                    group.Source = source;
                    group.Longest = call;
                }
            }
            if (groups.Count > 0)
            {
                builder.AppendLine("Submitted from (recorded Begin scope; CPU sums the selected invocations):");
                builder.AppendLine("calls  job_cpu_ms  longest_ms  calls_reference  source_reference  scope");
                var ordered = new List<SubmissionGroup>(groups.Values);
                ordered.Sort((left, right) => right.CpuMs.CompareTo(left.CpuMs));
                foreach (var group in ordered.GetRange(0, Math.Min(limit, ordered.Count)))
                {
                    AppendPadded(builder, group.Calls, 7);
                    AppendPadded(builder, ProfilerValueFormatter.FormatNumber(group.CpuMs), 12);
                    AppendPadded(builder, ProfilerValueFormatter.FormatNumber(group.Longest.DurationMs), 12);
                    builder.Append(MakeReference(frame, thread, 'c', $"{row.PublicId}~{group.Source.SourceKey}"))
                        .Append("  ").Append(reader.Reference(group.Source)).Append("  ")
                        .Append(group.Source.Thread.Label).Append("  ")
                        .AppendLine(ProfilerValueFormatter.FormatSampleName(group.Source.Name));
                }
                AppendOmitted(builder, ordered.Count, limit);
            }
            builder.Append("Flow coverage:");
            foreach (var entry in coverage)
                builder.Append($" {entry.Key}={entry.Value};");
            builder.AppendLine(" lookup limited to this frame and its available neighbours.");
            AppendCompletion(builder, reader, calls, limit);
        }

        static BridgeCommandResult BrowseCalls(int frame, string thread, HierarchyRow row, string key, string sort, int limit)
        {
            using var reader = new SampleReader(frame);
            var calls = reader.Calls(row);
            var separator = key.IndexOf('~');
            if (separator >= 0)
            {
                var sourceKey = key[(separator + 1)..];
                calls.RemoveAll(call => reader.Source(call, out _)?.SourceKey != sourceKey);
            }
            var scores = new Dictionary<SampleCall, double>();
            foreach (var call in calls)
                scores.Add(call, sort switch
                {
                    "self_ms" => CallSelfMs(call),
                    "gc_bytes" => CallGcBytes(call),
                    _ => call.DurationMs,
                });
            calls.Sort((left, right) =>
            {
                var result = sort switch
                {
                    "start_ms" => left.StartNs.CompareTo(right.StartNs),
                    _ => scores[right].CompareTo(scores[left]),
                };
                if (result != 0)
                    return result;
                result = left.Thread.View.threadIndex.CompareTo(right.Thread.View.threadIndex);
                return result != 0 ? result : left.Index.CompareTo(right.Index);
            });
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            AppendFrameIdentity(builder, frame, thread);
            builder.AppendLine($"{ProfilerValueFormatter.FormatSampleName(row.Name)}: {calls.Count} individual calls. Times are ms from this frame's start on all threads.");
            builder.AppendLine($"{"reference",-42}{"thread",-12}{"start_ms",-12}{"end_ms",-12}{"duration_ms",-13}submitted_from");
            for (var index = 0; index < Math.Min(limit, calls.Count); index++)
            {
                var call = calls[index];
                var source = reader.Source(call, out var status);
                AppendPadded(builder, reader.Reference(call), 42);
                AppendPadded(builder, call.Thread.Label, 12);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(RelativeMs(call.StartNs, reader.FrameStartNs)), 12);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(RelativeMs(call.EndNs, reader.FrameStartNs)), 12);
                AppendPadded(builder, ProfilerValueFormatter.FormatNumber(call.DurationMs), 13);
                builder.AppendLine(source == null ? status : ProfilerValueFormatter.FormatSampleName(source.Name));
            }
            AppendOmitted(builder, calls.Count, limit);
            return BridgeCommandResult.Success(builder.ToTrimmedString());
        }

        static BridgeCommandResult BrowseInvocation(SampleReference reference, int limit)
        {
            if (!reference.Thread.StartsWith("t", StringComparison.Ordinal)
                || !int.TryParse(reference.Thread.AsSpan(1), out var thread)
                || !int.TryParse(reference.Key, out var sample))
                throw new ArgumentException("Invalid invocation reference.");
            using var reader = new SampleReader(reference.Frame);
            var call = reader.Call(reference.Frame, thread, sample);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            AppendFrameIdentity(builder, reference.Frame, call.Thread.Label);
            builder.AppendLine(ProfilerValueFormatter.FormatSamplePath(call.Path));
            builder.AppendLine($"Thread: {call.Thread.View.threadGroupName}/{call.Thread.View.threadName}; index={thread}; id={call.Thread.View.threadId}");
            builder.AppendLine($"start_ms={ProfilerValueFormatter.FormatNumber(RelativeMs(call.StartNs, reader.FrameStartNs))}; end_ms={ProfilerValueFormatter.FormatNumber(RelativeMs(call.EndNs, reader.FrameStartNs))}; duration_ms={ProfilerValueFormatter.FormatNumber(call.DurationMs)}; self_ms={ProfilerValueFormatter.FormatNumber(CallSelfMs(call))}; gc_bytes={CallGcBytes(call)}");
            AppendFlowDetails(builder, reader, call, limit);
            AppendCompletion(builder, reader, new() { call }, limit);
            AppendEvidence(builder, call, limit);
            return BridgeCommandResult.Success(builder.ToTrimmedString());
        }

        static double RelativeMs(ulong time, ulong origin)
            => time >= origin ? (time - origin) / 1_000_000.0 : -((origin - time) / 1_000_000.0);

        static void AppendFlowDetails(StringBuilder builder, SampleReader reader, SampleCall call, int limit)
        {
            var ids = new HashSet<uint>();
            foreach (var flow in call.Thread.Flows(call.Index))
                ids.Add(flow.FlowId);
            foreach (var flow in call.Thread.ExecutionFlows(call.Index))
                ids.Add(flow.FlowId);
            if (ids.Count == 0)
            {
                builder.AppendLine("Flow links: none recorded for this call or its execution scope.");
                return;
            }
            var points = new List<(uint Id, FlowPoint Point)>();
            var missingBegins = 0;
            var missingEnds = 0;
            var ambiguous = 0;
            foreach (var id in ids)
            {
                var hasEnd = false;
                var status = FindFlowSource(reader.Flow(id), call.EndNs, out _);
                missingBegins += status == "missing begin" ? 1 : 0;
                ambiguous += status == "ambiguous" ? 1 : 0;
                foreach (var point in reader.Flow(id))
                {
                    points.Add((id, point));
                    hasEnd |= point.Type == ProfilerFlowEventType.End;
                }
                missingEnds += hasEnd ? 0 : 1;
            }
            points.Sort((left, right) => left.Point.StartNs.CompareTo(right.Point.StartNs));
            builder.AppendLine("Recorded flow endpoints (this frame and neighbours; times describe enclosing samples, not event timestamps):");
            builder.AppendLine("flow  event  unity_frame  thread  start_ms  duration_ms  reference  scope");
            for (var index = 0; index < Math.Min(limit, points.Count); index++)
            {
                var (id, point) = points[index];
                var endpoint = reader.Call(point.Frame, point.Thread, point.Sample);
                builder.Append($"{id}  {point.Type}  {point.Frame}  {endpoint.Thread.Label}  ")
                    .Append(ProfilerValueFormatter.FormatNumber(RelativeMs(endpoint.StartNs, reader.FrameStartNs))).Append("  ")
                    .Append(ProfilerValueFormatter.FormatNumber(endpoint.DurationMs)).Append("  ")
                    .Append(reader.Reference(endpoint)).Append("  ").Append(ProfilerValueFormatter.FormatSampleName(endpoint.Name));
                if (point.ExecutionSample >= 0 && point.ExecutionSample != point.Sample)
                    builder.Append("; job=").Append(reader.Reference(reader.Call(point.Frame, point.Thread, point.ExecutionSample)));
                builder.AppendLine();
            }
            AppendOmitted(builder, points.Count, limit);
            reader.Source(call, out var coverage);
            builder.AppendLine($"Submitting scope: {coverage}; flows without Begin={missingBegins}, without End={missingEnds}, ambiguous={ambiguous}. Missing endpoints may be outside these frames or absent from the capture.");
        }

        static void AppendCompletion(StringBuilder builder, SampleReader reader, List<SampleCall> calls, int limit)
        {
            var completions = new List<SampleCall>();
            foreach (var call in calls)
            {
                if (call.Thread.Label != "main")
                    continue;
                var raw = call.Thread.View;
                var end = call.Index + raw.GetSampleChildrenCountRecursive(call.Index);
                for (var index = call.Index; index <= end; index++)
                    if (raw.GetSampleName(index) is "JobHandle.Complete" or "WaitForJobGroupID")
                    {
                        completions.Add(reader.Call(raw.frameIndex, raw.threadIndex, index));
                        // a nested WaitForJobGroupID belongs to the enclosing Complete interval.
                        index += raw.GetSampleChildrenCountRecursive(index);
                    }
            }
            if (completions.Count == 0)
                return;
            var duration = 0.0;
            var idle = 0.0;
            var execution = 0.0;
            var unresolvedFlows = 0;
            var linked = new Dictionary<(int Frame, int Thread, int Sample), SampleCall>();
            foreach (var call in completions)
            {
                var raw = call.Thread.View;
                var end = call.Index + raw.GetSampleChildrenCountRecursive(call.Index);
                var idleSpans = new List<(ulong Start, ulong End)>();
                var executionSpans = new List<(ulong Start, ulong End)>();
                var completionFlows = new HashSet<uint>();
                for (var index = call.Index; index <= end; index++)
                {
                    var name = raw.GetSampleName(index);
                    var startNs = raw.GetSampleStartTimeNs(index);
                    var endNs = startNs + raw.GetSampleTimeNs(index);
                    if (name == "Idle")
                        idleSpans.Add((startNs, endNs));
                    if (call.Thread.JobScope(index) == index)
                        executionSpans.Add((startNs, endNs));
                    foreach (var flow in call.Thread.Flows(index))
                    {
                        if (flow.FlowEventType != ProfilerFlowEventType.End)
                            continue;
                        completionFlows.Add(flow.FlowId);
                    }
                }
                foreach (var id in completionFlows)
                {
                    var points = reader.Flow(id);
                    if (FindFlowSource(points, call.EndNs, out _) == "ambiguous")
                    {
                        unresolvedFlows++;
                        continue;
                    }
                    var hasExecution = false;
                    foreach (var point in points)
                        if (IsExecutionEvent(point.Type) && point.StartNs <= call.EndNs)
                        {
                            var sample = point.ExecutionSample < 0 ? point.Sample : point.ExecutionSample;
                            var related = reader.Call(point.Frame, point.Thread, sample);
                            linked.TryAdd((point.Frame, point.Thread, sample), related);
                            hasExecution = true;
                        }
                    unresolvedFlows += hasExecution ? 0 : 1;
                }
                var idleNs = UnionDuration(idleSpans, call.StartNs, call.EndNs);
                var combined = new List<(ulong Start, ulong End)>(idleSpans);
                combined.AddRange(executionSpans);
                // nested execution and idle scopes must occupy each nanosecond at most once.
                var executionNs = UnionDuration(combined, call.StartNs, call.EndNs) - idleNs;
                duration += call.DurationMs;
                idle += idleNs / 1_000_000.0;
                execution += executionNs / 1_000_000.0;
            }
            builder.AppendLine($"Completion totals ({completions.Count} scopes inside selection), ms: elapsed={ProfilerValueFormatter.FormatNumber(duration)}; recorded_idle={ProfilerValueFormatter.FormatNumber(idle)}; main_thread_jobs={ProfilerValueFormatter.FormatNumber(execution)}; unclassified={ProfilerValueFormatter.FormatNumber(Math.Max(0, duration - idle - execution))}");
            builder.AppendLine("Job execution uses recorded BeginJob/EndJob scopes; unclassified includes other work or waits.");
            if (unresolvedFlows > 0)
                builder.AppendLine($"Completion flows with ambiguous or missing execution: {unresolvedFlows}.");
            if (linked.Count == 0)
            {
                builder.AppendLine("No execution samples linked to a recorded completion End in the searched frames.");
                return;
            }
            var relatedCalls = new List<SampleCall>(linked.Values);
            relatedCalls.Sort((left, right) => right.DurationMs.CompareTo(left.DurationMs));
            builder.AppendLine("Execution samples sharing a recorded completion flow (full sample durations; not a critical-path estimate):");
            foreach (var call in relatedCalls.GetRange(0, Math.Min(limit, relatedCalls.Count)))
                builder.AppendLine($"{reader.Reference(call)}  {call.Thread.Label}  {ProfilerValueFormatter.FormatNumber(call.DurationMs)} ms  {ProfilerValueFormatter.FormatSampleName(call.Name)}");
            AppendOmitted(builder, relatedCalls.Count, limit);
        }

        /// <summary>Measures the union of intervals clipped to a scope, preserving nanosecond precision.</summary>
        internal static ulong UnionDuration(List<(ulong Start, ulong End)> intervals, ulong start, ulong end)
        {
            intervals.Sort((left, right) => left.Start.CompareTo(right.Start));
            var cursor = start;
            ulong total = 0;
            foreach (var interval in intervals)
            {
                var left = Math.Max(cursor, interval.Start);
                var right = Math.Min(end, interval.End);
                if (right <= left)
                    continue;
                total += right - left;
                cursor = right;
            }
            return total;
        }

        static double CallSelfMs(SampleCall call)
        {
            var raw = call.Thread.View;
            var spans = new List<(ulong Start, ulong End)>();
            var end = call.Index + raw.GetSampleChildrenCountRecursive(call.Index);
            for (var index = call.Index + 1; index <= end; index += raw.GetSampleChildrenCountRecursive(index) + 1)
            {
                var start = raw.GetSampleStartTimeNs(index);
                spans.Add((start, start + raw.GetSampleTimeNs(index)));
            }
            return call.DurationMs - UnionDuration(spans, call.StartNs, call.EndNs) / 1_000_000.0;
        }

        static long CallGcBytes(SampleCall call)
        {
            var raw = call.Thread.View;
            var gc = raw.GetMarkerId("GC.Alloc");
            long bytes = 0;
            var end = call.Index + raw.GetSampleChildrenCountRecursive(call.Index);
            for (var index = call.Index; index <= end; index++)
                if (raw.GetSampleMarkerId(index) == gc && raw.GetSampleMetadataCount(index) > 0)
                    bytes += raw.GetSampleMetadataAsLong(index, 0);
            return bytes;
        }

        static void AppendEvidence(StringBuilder builder, SampleCall call, int limit)
        {
            var raw = call.Thread.View;
            var metadata = raw.GetMarkerMetadataInfo(raw.GetSampleMarkerId(call.Index));
            var count = raw.GetSampleMetadataCount(call.Index);
            builder.AppendLine(count == 0 ? "Metadata: not recorded." : "Metadata:");
            for (var index = 0; index < Math.Min(count, limit); index++)
            {
                if (index >= metadata.Length)
                {
                    builder.AppendLine($"  [{index}]: recorded value has no type descriptor.");
                    continue;
                }
                var info = metadata[index];
                var value = info.type switch
                {
                    ProfilerMarkerDataType.Int32 => raw.GetSampleMetadataAsInt(call.Index, index).ToString(CultureInfo.InvariantCulture),
                    ProfilerMarkerDataType.UInt32 => unchecked((uint)raw.GetSampleMetadataAsInt(call.Index, index)).ToString(CultureInfo.InvariantCulture),
                    ProfilerMarkerDataType.Int64 => raw.GetSampleMetadataAsLong(call.Index, index).ToString(CultureInfo.InvariantCulture),
                    ProfilerMarkerDataType.UInt64 => unchecked((ulong)raw.GetSampleMetadataAsLong(call.Index, index)).ToString(CultureInfo.InvariantCulture),
                    ProfilerMarkerDataType.Float => raw.GetSampleMetadataAsFloat(call.Index, index).ToString("R", CultureInfo.InvariantCulture),
                    ProfilerMarkerDataType.Double => raw.GetSampleMetadataAsDouble(call.Index, index).ToString("R", CultureInfo.InvariantCulture),
                    ProfilerMarkerDataType.String16 => raw.GetSampleMetadataAsString(call.Index, index),
                    _ => FormatMetadataBytes(raw.GetSampleMetadataAsSpan<byte>(call.Index, index), limit),
                };
                builder.AppendLine($"  {info.name} ({info.type}, {info.unit}): {BoundedText(value, limit * 16)}");
            }
            AppendOmitted(builder, count, limit);
            using var pooledStack = ConduitPool.GetPooledList<ulong>(out var stack);
            raw.GetSampleCallstack(call.Index, stack);
            builder.AppendLine(stack.Count == 0 ? "Call stack: not recorded." : "Call stack:");
            for (var index = 0; index < Math.Min(stack.Count, limit); index++)
            {
                var method = raw.ResolveMethodInfo(stack[index]);
                builder.Append("  ").Append(string.IsNullOrEmpty(method.methodName) ? $"0x{stack[index]:x}" : method.methodName);
                if (!string.IsNullOrEmpty(method.sourceFileName))
                    builder.Append($" ({method.sourceFileName}:{method.sourceFileLine})");
                builder.AppendLine();
            }
            AppendOmitted(builder, stack.Count, limit);
        }

        static string FormatMetadataBytes(Span<byte> bytes, int limit)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < Math.Min(bytes.Length, limit); index++)
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            if (bytes.Length > limit)
                builder.Append($"... ({bytes.Length - limit} bytes omitted; increase limit)");
            return builder.ToString();
        }

        static string BoundedText(string value, int limit)
        {
            value = value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return value.Length <= limit ? value : $"{value[..limit]}... ({value.Length - limit} characters omitted; increase limit)";
        }

        static void AppendOmitted(StringBuilder builder, int count, int limit)
        {
            if (count > limit)
                builder.AppendLine($"{count - limit} rows omitted; increase limit to show more.");
        }
    }
}
