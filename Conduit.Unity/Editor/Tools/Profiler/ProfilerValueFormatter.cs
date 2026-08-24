#nullable enable

using System;
using System.Globalization;

namespace Conduit
{
    static class ProfilerValueFormatter
    {
        internal static string FormatFrameOrdinal(int frameOrdinal, int frameIndex)
            => (frameOrdinal >= 0 ? frameOrdinal : frameIndex).ToString(CultureInfo.InvariantCulture);

        internal static string FormatSamplePath(string displayPath)
        {
            if (string.IsNullOrWhiteSpace(displayPath))
                return "<unnamed>";

            var segments = displayPath.Split('/');
            var firstDetailedSegmentIndex = Math.Max(0, segments.Length - 3);
            for (var i = 0; i < segments.Length; i++)
                segments[i] = FormatSampleSegment(segments[i], keepNamespace: i >= firstDetailedSegmentIndex);

            return string.Join("/", segments);
        }

        internal static string FormatSampleName(string name) => FormatSampleSegment(name, keepNamespace: true);

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

        internal static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        internal static string FormatOptionalNumber(double value) => value <= 0 ? "n/a" : FormatNumber(value);

        internal static string FormatKb(double bytes)
        {
            var kb = bytes / 1024.0;
            return bytes > 0 && kb < 0.1 ? "<0.1" : kb.ToString("0.#", CultureInfo.InvariantCulture);
        }

        internal static string FormatMb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);

        internal static string FormatPercent(double ms, double frameMs)
            => frameMs <= 0 ? "0" : (ms / frameMs * 100.0).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
