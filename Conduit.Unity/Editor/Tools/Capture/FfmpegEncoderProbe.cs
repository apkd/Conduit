#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Conduit
{
    static class FfmpegEncoderProbe
    {
        static readonly object cacheGate = new();
        static readonly Dictionary<string, ProbeResult> probeCache = new(StringComparer.Ordinal);

        internal static ProbeResult Probe(FfmpegEncoderSpec spec)
        {
            lock (cacheGate)
                if (probeCache.TryGetValue(spec.ProbeKey, out var cached))
                    return cached;

            var result = ProbeCore(spec);
            if (result.Cacheable)
                lock (cacheGate)
                    probeCache[spec.ProbeKey] = result;

            return result;
        }

        static ProbeResult ProbeCore(FfmpegEncoderSpec spec)
        {
            try
            {
                var arguments = new List<string>
                {
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                };
                arguments.AddRange(spec.PreInputArguments);
                arguments.AddRange(
                    new[]
                    {
                        "-f", "lavfi",
                        "-i", "color=c=black:s=256x256:r=1:d=1",
                        "-frames:v", "1",
                        "-an",
                        "-vf", spec.ProbeFilter,
                    }
                );
                arguments.AddRange(spec.CodecArguments);
                arguments.AddRange(new[] { "-f", "null", "-" });

                var startInfo = new ProcessStartInfo
                {
                    FileName = FfmpegExecutable.Path,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var argument in arguments)
                    startInfo.ArgumentList.Add(argument);

                using var process = Process.Start(startInfo)
                                    ?? throw new InvalidOperationException("FFmpeg did not start.");
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15_000))
                {
                    try { process.Kill(); }
                    catch { }

                    return new(false, "probe timed out", cacheable: false);
                }

                var stderr = stderrTask.GetAwaiter().GetResult().Trim();
                return process.ExitCode == 0
                    ? new(true, string.Empty, cacheable: true)
                    : new(false, CondenseDiagnostic(stderr), cacheable: true);
            }
            catch (Exception exception)
            {
                return new(false, exception.Message, cacheable: false);
            }
        }

        static string CondenseDiagnostic(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "encoder probe failed";

            const int maximumLength = 800;
            var trimmed = value.Trim();
            return trimmed.Length <= maximumLength
                ? trimmed
                : trimmed[(trimmed.Length - maximumLength)..];
        }

        internal readonly struct ProbeResult
        {
            internal ProbeResult(bool supported, string diagnostic, bool cacheable)
            {
                Supported = supported;
                Diagnostic = diagnostic;
                Cacheable = cacheable;
            }

            internal bool Supported { get; }
            internal string Diagnostic { get; }
            internal bool Cacheable { get; }
        }
    }
}
