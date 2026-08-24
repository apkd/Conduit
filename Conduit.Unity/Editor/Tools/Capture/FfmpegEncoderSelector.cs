#nullable enable

using System;

namespace Conduit
{
    static class FfmpegEncoderSelector
    {
        internal static FfmpegEncoderSpec Select(string format, int crf)
        {
            var candidates = FfmpegEncoderCatalog.BuildCandidates(format, crf);
            using var pooledDiagnostic = ConduitPool.GetStringBuilder(out var diagnostic);
            foreach (var candidate in candidates)
            {
                var probe = FfmpegEncoderProbe.Probe(candidate);
                if (probe.Supported)
                    return candidate;

                diagnostic.Append(candidate.DisplayName)
                    .Append(": ")
                    .AppendLine(probe.Diagnostic);
            }

            var detail = diagnostic.ToTrimmedString();
            throw new InvalidOperationException(
                $"No working FFmpeg encoder was found for format '{format}'. {detail}"
            );
        }
    }
}
