#nullable enable

using System;
using System.Globalization;

namespace Conduit
{
    readonly struct RecordSettings
    {
        internal RecordSettings(
            string target,
            double durationSeconds,
            bool adjustDeltaTime,
            int frameRate,
            float resolutionScale,
            string format,
            int crf)
        {
            Target = target;
            DurationSeconds = durationSeconds;
            AdjustDeltaTime = adjustDeltaTime;
            FrameRate = frameRate;
            ResolutionScale = resolutionScale;
            Format = format;
            Crf = crf;
            FrameCount = checked((int)Math.Ceiling(durationSeconds * frameRate));
        }

        internal string Target { get; }
        internal double DurationSeconds { get; }
        internal bool AdjustDeltaTime { get; }
        internal int FrameRate { get; }
        internal float ResolutionScale { get; }
        internal string Format { get; }
        internal int Crf { get; }
        internal int FrameCount { get; }

        internal static RecordSettings Parse(string? target, string[] args)
        {
            var normalizedTarget = target?.Trim() ?? string.Empty;
            if (normalizedTarget.Length == 0)
                throw new InvalidOperationException("Recording target was empty.");

            var duration = ParseDouble("duration_seconds");
            var adjustDeltaTime = ParseBool("adjust_delta_time");
            var frameRate = ParseInt("frame_rate");
            var resolutionScale = ParseFloat("resolution_scale");
            var format = Get("format").Trim().ToLowerInvariant();
            var crf = ParseInt("crf");

            if (!double.IsFinite(duration) || duration <= 0d || duration > 1800d)
                throw new InvalidOperationException("durationSeconds must be greater than zero and at most 1800.");

            if (frameRate is < 1 or > 240)
                throw new InvalidOperationException("frameRate must be from 1 through 240.");

            if (!float.IsFinite(resolutionScale) || resolutionScale is < 0.1f or > 1f)
                throw new InvalidOperationException("resolution_scale must be from 0.1 through 1.0.");

            if (format is not ("auto" or "x264" or "x265" or "x264_hw" or "x265_hw" or "webm" or "gif"))
                throw new InvalidOperationException(
                    "format must be auto, x264, x265, x264_hw, x265_hw, webm, or gif."
                );

            var maximumCrf = format == "webm" ? 63 : 51;
            if (format != "gif" && (crf is < 0 || crf > maximumCrf))
                throw new InvalidOperationException($"crf must be from 0 through {maximumCrf} for {format}.");

            return new(
                normalizedTarget,
                duration,
                adjustDeltaTime,
                frameRate,
                resolutionScale,
                format,
                crf
            );

            string Get(string key)
            {
                var prefix = key + "=";
                foreach (var argument in args)
                    if (argument.StartsWith(prefix, StringComparison.Ordinal))
                        return argument[prefix.Length..];

                throw new InvalidOperationException($"Missing record argument '{key}'.");
            }

            int ParseInt(string key)
                => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new InvalidOperationException($"Record argument '{key}' was not an integer.");

            float ParseFloat(string key)
                => float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new InvalidOperationException($"Record argument '{key}' was not a number.");

            double ParseDouble(string key)
                => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new InvalidOperationException($"Record argument '{key}' was not a number.");

            bool ParseBool(string key)
                => bool.TryParse(Get(key), out var value)
                    ? value
                    : throw new InvalidOperationException($"Record argument '{key}' was not true or false.");
        }
    }
}
