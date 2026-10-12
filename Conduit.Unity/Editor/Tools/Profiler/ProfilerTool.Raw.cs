#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Profiling;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace Conduit
{
    static partial class ProfilerTool
    {
        /// <summary>Owns native frame views for one read request; no views survive capture changes.</summary>
        sealed class SampleReader : IDisposable
        {
            readonly Dictionary<(int Frame, int Thread), SampleThread> threads = new();
            readonly Dictionary<(int Frame, int Thread, int Sample), SampleCall> calls = new();
            readonly Dictionary<uint, List<FlowPoint>> flows = new();
            readonly Dictionary<int, string> frameTokens = new();
            bool flowsRead;
            internal readonly int Frame;
            internal readonly ulong FrameStartNs;

            internal SampleReader(int frame)
            {
                Frame = frame;
                FrameStartNs = Thread(frame, 0).View.frameStartTimeNs;
            }

            internal SampleThread Thread(int frame, int thread)
            {
                if (threads.TryGetValue((frame, thread), out var value))
                    return value;
                var view = ProfilerDriver.GetRawFrameDataView(frame, thread);
                if (!view.valid)
                {
                    view.Dispose();
                    throw new InvalidOperationException("The referenced profiler thread is no longer available.");
                }
                value = new(view);
                threads.Add((frame, thread), value);
                return value;
            }

            internal SampleCall Call(int frame, int thread, int sample)
            {
                if (!calls.TryGetValue((frame, thread, sample), out var call))
                {
                    call = new(Thread(frame, thread), sample);
                    calls.Add((frame, thread, sample), call);
                }
                return call;
            }

            internal List<SampleCall> Calls(HierarchyRow row)
            {
                var result = new List<SampleCall>();
                foreach (var source in row.Sources)
                    result.Add(Call(Frame, source.Thread, source.Sample));
                return result;
            }

            internal string Reference(SampleCall call)
            {
                var frame = call.Thread.View.frameIndex;
                if (!frameTokens.TryGetValue(frame, out var token))
                    frameTokens.Add(frame, token = GetFrameToken(frame));
                return FormatReference(token, frame, $"t{call.Thread.View.threadIndex}", 's', call.Index.ToString(CultureInfo.InvariantCulture));
            }

            internal IReadOnlyList<FlowPoint> Flow(uint id)
            {
                ReadFlows();
                return flows.TryGetValue(id, out var points) ? points : Array.Empty<FlowPoint>();
            }

            void ReadFlows()
            {
                if (flowsRead)
                    return;
                flowsRead = true;
                var available = GetAvailableFrames();
                var ordinal = available.IndexOf(Frame);
                // adjacent frames cover common cross-frame schedules without scanning a whole capture per click.
                for (var offset = Math.Max(0, ordinal - 1); offset <= Math.Min(available.Count - 1, ordinal + 1); offset++)
                {
                    var frame = available[offset];
                    for (var threadIndex = 0; ; threadIndex++)
                    {
                        if (!threads.TryGetValue((frame, threadIndex), out var thread))
                        {
                            var raw = ProfilerDriver.GetRawFrameDataView(frame, threadIndex);
                            if (!raw.valid)
                            {
                                raw.Dispose();
                                break;
                            }
                            thread = new(raw);
                            threads.Add((frame, threadIndex), thread);
                        }
                        foreach (var point in thread.FlowEvents)
                        {
                            if (point.ParentSampleIndex < 0 || point.ParentSampleIndex >= thread.View.sampleCount)
                                continue;
                            if (!flows.TryGetValue(point.FlowId, out var points))
                                flows.Add(point.FlowId, points = new());
                            points.Add(new(frame, threadIndex, point.ParentSampleIndex, point.FlowEventType,
                                thread.View.GetSampleStartTimeNs(point.ParentSampleIndex),
                                IsExecutionEvent(point.FlowEventType) ? thread.JobScope(point.ParentSampleIndex) : -1));
                        }
                    }
                }
            }

            internal SampleCall? Source(SampleCall call, out string coverage)
            {
                SampleCall? source = null;
                var executionFlows = call.Thread.ExecutionFlows(call.Index);
                if (executionFlows.Count == 0)
                {
                    coverage = "unlinked";
                    return null;
                }
                foreach (var flow in executionFlows)
                {
                    var status = FindFlowSource(Flow(flow.FlowId), call.EndNs, out var point);
                    if (status != "linked")
                    {
                        coverage = status;
                        return null;
                    }
                    var candidate = Call(point.Frame, point.Thread, point.Sample);
                    if (source != null && !ReferenceEquals(source, candidate))
                    {
                        coverage = "ambiguous";
                        return null;
                    }
                    source = candidate;
                }
                coverage = "linked";
                return source;
            }

            public void Dispose()
            {
                foreach (var thread in threads.Values)
                    thread.View.Dispose();
            }
        }

        sealed class SampleThread
        {
            internal readonly RawFrameDataView View;
            internal readonly List<RawFrameDataView.FlowEvent> FlowEvents = new();
            readonly Dictionary<int, List<RawFrameDataView.FlowEvent>> flowsBySample = new();
            Dictionary<int, List<RawFrameDataView.FlowEvent>>? executionFlows;
            int[]? parents;
            int[]? jobScopes;
            readonly Dictionary<int, string> paths = new();

            internal SampleThread(RawFrameDataView view)
            {
                View = view;
                view.GetFlowEvents(FlowEvents);
                foreach (var flow in FlowEvents)
                {
                    if (!flowsBySample.TryGetValue(flow.ParentSampleIndex, out var sampleFlows))
                        flowsBySample.Add(flow.ParentSampleIndex, sampleFlows = new());
                    sampleFlows.Add(flow);
                }
            }

            internal IReadOnlyList<RawFrameDataView.FlowEvent> Flows(int sample)
                => flowsBySample.TryGetValue(sample, out var result) ? result : Array.Empty<RawFrameDataView.FlowEvent>();

            internal List<RawFrameDataView.FlowEvent> ExecutionFlows(int sample)
            {
                if (executionFlows == null)
                {
                    executionFlows = new();
                    foreach (var flow in FlowEvents)
                    {
                        if (!IsExecutionEvent(flow.FlowEventType) || flow.ParentSampleIndex < 0 || flow.ParentSampleIndex >= View.sampleCount)
                            continue;
                        var scope = JobScope(flow.ParentSampleIndex);
                        if (scope < 0)
                            scope = flow.ParentSampleIndex;
                        if (!executionFlows.TryGetValue(scope, out var points))
                            executionFlows.Add(scope, points = new());
                        points.Add(flow);
                    }
                }
                var result = new List<RawFrameDataView.FlowEvent>();
                var job = JobScope(sample);
                for (var current = job >= 0 ? job : sample; current >= 0; current = Parent(current))
                {
                    if (executionFlows.TryGetValue(current, out var points))
                        result.AddRange(points);
                    if (result.Count > 0)
                        break;
                }
                return result;
            }

            internal int JobScope(int sample)
            {
                if (jobScopes == null)
                    jobScopes = BuildJobScopeIndex(View.sampleCount, View.GetSampleName);
                return jobScopes[sample];
            }

            internal int Parent(int sample)
            {
                if (parents == null)
                {
                    parents = new int[View.sampleCount];
                    var stack = new Stack<(int Sample, int End)>();
                    for (var index = 0; index < parents.Length; index++)
                    {
                        while (stack.Count > 0 && index > stack.Peek().End)
                            stack.Pop();
                        parents[index] = stack.Count == 0 ? -1 : stack.Peek().Sample;
                        var children = View.GetSampleChildrenCountRecursive(index);
                        if (children > 0)
                            stack.Push((index, index + children));
                    }
                }
                return parents[sample];
            }

            internal string Path(int sample)
            {
                if (paths.TryGetValue(sample, out var path))
                    return path;
                var parent = Parent(sample);
                path = parent < 0 ? View.GetSampleName(sample) : $"{Path(parent)}/{View.GetSampleName(sample)}";
                paths.Add(sample, path);
                return path;
            }

            internal string Label => ProfilerThreadLabels.ClassifyThread(View.threadName, View.threadGroupName)
                                     ?? $"{View.threadGroupName}/{View.threadName}";
        }

        sealed class SampleCall
        {
            internal readonly SampleThread Thread;
            internal readonly int Index;
            internal readonly ulong StartNs;
            internal readonly ulong EndNs;
            internal double DurationMs => (EndNs - StartNs) / 1_000_000.0;
            internal string Name => Thread.View.GetSampleName(Index);
            internal string Path => Thread.Path(Index);
            internal string SourceKey => UnityEngine.Hash128.Compute($"{Thread.View.frameIndex}:{Thread.View.threadId}:{Path}").ToString()[..12];

            internal SampleCall(SampleThread thread, int index)
            {
                if (index < 0 || index >= thread.View.sampleCount)
                    throw new ArgumentException("The referenced invocation is not available.");
                Thread = thread;
                Index = index;
                StartNs = thread.View.GetSampleStartTimeNs(index);
                EndNs = StartNs + thread.View.GetSampleTimeNs(index);
            }
        }

        internal readonly struct FlowPoint
        {
            internal readonly int Frame, Thread, Sample, ExecutionSample;
            internal readonly ProfilerFlowEventType Type;
            internal readonly ulong StartNs;

            internal FlowPoint(int frame, int thread, int sample, ProfilerFlowEventType type, ulong startNs, int executionSample = -1)
                => (Frame, Thread, Sample, Type, StartNs, ExecutionSample) = (frame, thread, sample, type, startNs, executionSample);
        }

        static bool IsExecutionEvent(ProfilerFlowEventType type)
            => type is ProfilerFlowEventType.Next or ProfilerFlowEventType.ParallelNext;

        /// <summary>Maps scheduler end markers and nested samples to the enclosing job execution.</summary>
        internal static int[] BuildJobScopeIndex(int count, Func<int, string> nameAt)
        {
            // Unity's execution flow can be attached to EndJob or to an inner Burst marker.
            var scopes = new int[count];
            var jobs = new Stack<int>();
            for (var index = 0; index < scopes.Length; index++)
            {
                var name = nameAt(index);
                scopes[index] = -1;
                if (name == "BeginJob")
                    jobs.Push(-1);
                else if (name == "EndJob")
                {
                    if (jobs.Count > 0)
                        scopes[index] = jobs.Pop();
                }
                else if (jobs.Count > 0)
                {
                    if (jobs.Peek() < 0)
                    {
                        jobs.Pop();
                        jobs.Push(index);
                    }
                    scopes[index] = jobs.Peek();
                }
            }
            return scopes;
        }

        /// <summary>Reports a submitting scope only when the recorded begins identify one unambiguous sample.</summary>
        internal static string FindFlowSource(IReadOnlyList<FlowPoint> points, ulong executionEndNs, out FlowPoint source)
        {
            source = default;
            var found = false;
            foreach (var point in points)
            {
                if (point.Type != ProfilerFlowEventType.Begin || point.StartNs > executionEndNs)
                    continue;
                if (found && (source.Frame != point.Frame || source.Thread != point.Thread || source.Sample != point.Sample))
                    return "ambiguous";
                source = point;
                found = true;
            }
            return found ? "linked" : "missing begin";
        }
    }
}
