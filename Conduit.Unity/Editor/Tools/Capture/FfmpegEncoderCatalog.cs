#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Conduit
{
    static class FfmpegEncoderCatalog
    {
        internal static IReadOnlyList<FfmpegEncoderSpec> BuildCandidates(string format, int crf)
        {
            if (format == "gif")
                return new[] { Gif() };

            if (format == "webm")
                return new[] { Webm(crf) };

            if (format == "x264")
                return new[] { X264(crf) };

            if (format == "x265")
                return new[] { X265(crf) };

            var h264Hardware = HardwareCandidates(h265: false, crf);
            var h265Hardware = HardwareCandidates(h265: true, crf);
            if (format == "x264_hw")
                return h264Hardware;

            if (format == "x265_hw")
                return h265Hardware;

            var automatic = new List<FfmpegEncoderSpec>(
                h265Hardware.Count + h264Hardware.Count + 2
            );
            automatic.AddRange(h265Hardware);
            automatic.AddRange(h264Hardware);
            automatic.Add(X265(crf));
            automatic.Add(X264(crf));
            return automatic;
        }

        static List<FfmpegEncoderSpec> HardwareCandidates(bool h265, int crf)
        {
            var candidates = new List<FfmpegEncoderSpec>();
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                var quality = Mathf.Clamp(100 - Mathf.RoundToInt(crf * 100f / 51f), 0, 100);
                candidates.Add(
                    new(
                        h265 ? "hevc_videotoolbox" : "h264_videotoolbox",
                        h265 ? "HEVC VideoToolbox" : "H.264 VideoToolbox",
                        "vflip,format=nv12",
                        Array.Empty<string>(),
                        new[]
                        {
                            "-c:v", h265 ? "hevc_videotoolbox" : "h264_videotoolbox",
                            "-allow_sw", "0",
                            "-q:v", quality.ToString(),
                            "-tag:v", h265 ? "hvc1" : "avc1",
                            "-movflags", "+faststart",
                        },
                        isGif: false
                    )
                );
                return candidates;
            }

            candidates.Add(
                new(
                    h265 ? "hevc_nvenc" : "h264_nvenc",
                    h265 ? "HEVC NVENC" : "H.264 NVENC",
                    "vflip,format=nv12",
                    Array.Empty<string>(),
                    new[]
                    {
                        "-c:v", h265 ? "hevc_nvenc" : "h264_nvenc",
                        "-preset", "p4",
                        "-tune", "hq",
                        "-rc", "vbr",
                        "-cq", crf.ToString(),
                        "-b:v", "0",
                        "-tag:v", h265 ? "hvc1" : "avc1",
                        "-movflags", "+faststart",
                    },
                    isGif: false
                )
            );
            candidates.Add(
                new(
                    h265 ? "hevc_qsv" : "h264_qsv",
                    h265 ? "HEVC Quick Sync" : "H.264 Quick Sync",
                    "vflip,format=nv12",
                    Array.Empty<string>(),
                    new[]
                    {
                        "-c:v", h265 ? "hevc_qsv" : "h264_qsv",
                        "-global_quality", crf.ToString(),
                        "-preset", "veryfast",
                        "-tag:v", h265 ? "hvc1" : "avc1",
                        "-movflags", "+faststart",
                    },
                    isGif: false
                )
            );

            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var qpI = Mathf.Clamp(crf + 2, 0, 51).ToString();
                var qpB = Mathf.Clamp(crf + 4, 0, 51).ToString();
                var codecArguments = new List<string>
                {
                    "-c:v", h265 ? "hevc_amf" : "h264_amf",
                    "-quality", "speed",
                    "-rc", "cqp",
                    "-qp_i", qpI,
                    "-qp_p", qpI,
                };
                if (!h265)
                    codecArguments.AddRange(new[] { "-qp_b", qpB });

                codecArguments.AddRange(
                    new[]
                    {
                        "-tag:v", h265 ? "hvc1" : "avc1",
                        "-movflags", "+faststart",
                    }
                );
                candidates.Add(
                    new(
                        h265 ? "hevc_amf" : "h264_amf",
                        h265 ? "HEVC AMF" : "H.264 AMF",
                        "vflip,format=nv12",
                        Array.Empty<string>(),
                        codecArguments.ToArray(),
                        isGif: false
                    )
                );
            }

            if (Application.platform == RuntimePlatform.LinuxEditor
                && TryFindVaapiDevice(out var device))
                candidates.Add(
                    new(
                        h265 ? "hevc_vaapi" : "h264_vaapi",
                        h265 ? "HEVC VAAPI" : "H.264 VAAPI",
                        "vflip,format=nv12,hwupload",
                        new[] { "-vaapi_device", device },
                        new[]
                        {
                            "-c:v", h265 ? "hevc_vaapi" : "h264_vaapi",
                            "-qp", Mathf.Clamp(crf + 2, 0, 51).ToString(),
                            "-tag:v", h265 ? "hvc1" : "avc1",
                            "-movflags", "+faststart",
                        },
                        isGif: false
                    )
                );

            return candidates;
        }

        static FfmpegEncoderSpec X264(int crf)
            => new(
                "libx264",
                "libx264",
                "vflip,format=yuv420p",
                Array.Empty<string>(),
                new[]
                {
                    "-c:v", "libx264",
                    "-preset", "veryfast",
                    "-crf", crf.ToString(),
                    "-movflags", "+faststart",
                },
                isGif: false
            );

        static FfmpegEncoderSpec X265(int crf)
            => new(
                "libx265",
                "libx265",
                "vflip,format=yuv420p",
                Array.Empty<string>(),
                new[]
                {
                    "-c:v", "libx265",
                    "-preset", "fast",
                    "-crf", crf.ToString(),
                    "-x265-params", "log-level=error",
                    "-tag:v", "hvc1",
                    "-movflags", "+faststart",
                },
                isGif: false
            );

        static FfmpegEncoderSpec Webm(int crf)
            => new(
                "libvpx-vp9",
                "libvpx-vp9",
                "vflip,format=yuv420p",
                Array.Empty<string>(),
                new[]
                {
                    "-c:v", "libvpx-vp9",
                    "-deadline", "realtime",
                    "-cpu-used", "4",
                    "-crf", crf.ToString(),
                    "-b:v", "0",
                },
                isGif: false
            );

        static FfmpegEncoderSpec Gif()
            => new(
                "ffv1",
                "FFV1 with global GIF palette",
                "vflip,format=bgr0",
                Array.Empty<string>(),
                new[]
                {
                    "-c:v", "ffv1",
                    "-level", "3",
                    "-coder", "1",
                    "-context", "1",
                    "-g", "1",
                },
                isGif: true
            );

        static bool TryFindVaapiDevice(out string device)
        {
            try
            {
                var devices = Directory.GetFiles("/dev/dri", "renderD*");
                Array.Sort(devices, StringComparer.Ordinal);
                if (devices.Length > 0)
                {
                    device = devices[0];
                    return true;
                }
            }
            catch { }

            device = string.Empty;
            return false;
        }
    }
}
