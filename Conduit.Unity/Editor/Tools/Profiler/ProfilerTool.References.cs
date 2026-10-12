#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditorInternal;
using UnityEngine;

namespace Conduit
{
    static partial class ProfilerTool
    {
        static string captureId = Guid.NewGuid().ToString("N")[..12];

        static ProfilerTool()
        {
            // also catches captures loaded or cleared through Unity's own profiler window.
            ProfilerDriver.profileLoaded += InvalidateReferences;
            ProfilerDriver.profileCleared += InvalidateReferences;
        }

        static void InvalidateReferences() => captureId = Guid.NewGuid().ToString("N")[..12];

        internal readonly struct SampleReference
        {
            internal readonly int Frame;
            internal readonly string Thread;
            internal readonly char Kind;
            internal readonly string Key;

            internal SampleReference(int frame, string thread, char kind, string key)
                => (Frame, Thread, Kind, Key) = (frame, thread, kind, key);
        }

        static string GetFrameToken(int frame)
        {
            using var raw = ProfilerDriver.GetRawFrameDataView(frame, 0);
            if (!raw.valid)
                throw new InvalidOperationException("The referenced frame is no longer available. Query the current capture again.");
            return Hash128.Compute($"{captureId}:{frame}:{raw.frameStartTimeNs}:{raw.sampleCount}").ToString()[..12];
        }

        static string MakeReference(int frame, string thread, char kind, string key)
            => FormatReference(GetFrameToken(frame), frame, thread, kind, key);

        internal static string FormatReference(string token, int frame, string thread, char kind, string key)
            => $"p:{token}:{frame.ToString(CultureInfo.InvariantCulture)}:{thread}:{kind}{key}";

        internal static SampleReference ParseReference(string selector, string expectedToken)
        {
            var parts = selector.Split(':');
            if (parts.Length != 5 || parts[0] != "p" || parts[4].Length < 2
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame)
                || parts[4][0] is not ('h' or 'c' or 's'))
                throw new ArgumentException("Invalid profiler reference. Copy a reference from the current report.");
            if (parts[1] != expectedToken)
                throw new InvalidOperationException("Stale profiler reference: its capture or frame was replaced. Query the current capture again.");
            return new(frame, parts[3], parts[4][0], parts[4][1..]);
        }

        static bool TryParseReference(string selector, out SampleReference reference)
        {
            reference = default;
            if (!selector.StartsWith("p:", StringComparison.Ordinal))
                return false;
            var parts = selector.Split(':');
            if (parts.Length != 5 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
                throw new ArgumentException("Invalid profiler reference.");
            reference = ParseReference(selector, GetFrameToken(frame));
            return true;
        }

        static (string Path, uint Identity) ResolveReferencedIdentity(SampleReference reference)
        {
            if (!TryBuildBrowseHierarchy(reference.Frame, reference.Thread, "total_ms", out var root,
                    out _, out _, out _, out var diagnostic, includeSamples: false))
                throw new InvalidOperationException(diagnostic);
            using var pooledRows = ConduitPool.GetPooledList<HierarchyRow>(out var rows);
            Flatten(root, rows);
            AssignPublicIds(rows);
            var selected = ResolveRoot(root, rows, reference.Key, new List<string>());
            if (selected == null)
                throw new InvalidOperationException("The referenced sample is no longer available.");
            return (BuildDisplayPath(selected), selected.IdentityHash);
        }

        static void AppendFrameIdentity(StringBuilder builder, int frame, string thread)
        {
            builder.AppendLine($"Capture: {captureId}; frame: {GetAvailableFrames().IndexOf(frame)}; Unity index: {frame}; thread: {thread}");
        }
    }
}
