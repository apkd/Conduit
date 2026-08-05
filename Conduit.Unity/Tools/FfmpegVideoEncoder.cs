#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace Conduit
{
    /// <summary>Streams native RGBA frames into an isolated FFmpeg encoder process.</summary>
    sealed class FfmpegVideoEncoder : IDisposable
    {
        const int ProcessTimeoutMilliseconds = 30 * 60 * 1000;
        const int MaximumDiagnosticCharacters = 32 * 1024;

        readonly object diagnosticGate = new();
        readonly object processGate = new();
        readonly StringBuilder diagnostic = new();
        readonly FfmpegEncoderSpec spec;
        readonly RecordOutputPath outputPath;
        Process? process;
        bool aborted;
        bool finished;

        FfmpegVideoEncoder(
            FfmpegEncoderSpec spec,
            RecordOutputPath outputPath,
            int width,
            int height,
            int frameRate)
        {
            this.spec = spec;
            this.outputPath = outputPath;
            process = StartPrimaryProcess(width, height, frameRate);
        }

        public string EncoderName => spec.DisplayName;

        public static FfmpegVideoEncoder Start(
            RecordSettings settings,
            RecordOutputPath outputPath,
            int width,
            int height)
        {
            var spec = FfmpegEncoderSelector.Select(settings.Format, settings.Crf);
            try
            {
                return new(spec, outputPath, width, height, settings.FrameRate);
            }
            catch
            {
                outputPath.DeleteTemporaryFiles();
                throw;
            }
        }

        public void WriteFrame(NativeArray<byte> frame)
        {
            var runningProcess = GetRunningProcess();
            runningProcess.StandardInput.BaseStream.Write(frame.AsReadOnlySpan());
        }

        public void Finish()
        {
            var runningProcess = GetRunningProcess();
            try
            {
                runningProcess.StandardInput.Close();
                WaitForSuccessfulExit(runningProcess, "video encoding");
                ClearProcess(runningProcess);
                runningProcess.Dispose();

                if (spec.IsGif)
                    FinishGif();
                else
                    CompleteOutput(outputPath.PartialPath);
            }
            catch
            {
                outputPath.DeleteTemporaryFiles();
                throw;
            }
        }

        public void Abort()
        {
            Process? runningProcess;
            lock (processGate)
            {
                if (finished || aborted)
                    return;

                aborted = true;
                runningProcess = process;
            }

            if (runningProcess == null)
                return;

            try { runningProcess.StandardInput.Close(); }
            catch { }

            try
            {
                if (!runningProcess.HasExited)
                    runningProcess.Kill();
            }
            catch { }
        }

        public void Dispose()
        {
            Abort();
            Process? runningProcess;
            lock (processGate)
            {
                runningProcess = process;
                process = null;
            }

            runningProcess?.Dispose();
            outputPath.DeleteTemporaryFiles();
        }

        Process StartPrimaryProcess(int width, int height, int frameRate)
        {
            var arguments = new List<string>
            {
                "-hide_banner",
                "-nostdin",
                "-loglevel", "warning",
                "-y",
            };
            arguments.AddRange(spec.PreInputArguments);
            arguments.AddRange(
                new[]
                {
                    "-f", "rawvideo",
                    "-pixel_format", "rgba",
                    "-video_size", $"{width}x{height}",
                    "-framerate", frameRate.ToString(),
                    "-i", "pipe:0",
                    "-an",
                    "-vf", spec.InputFilter,
                }
            );
            arguments.AddRange(spec.CodecArguments);
            arguments.Add(spec.IsGif ? outputPath.IntermediatePath : outputPath.PartialPath);

            var startInfo = CreateStartInfo(arguments, redirectInput: true);
            var startedProcess = Process.Start(startInfo)
                                 ?? throw new InvalidOperationException("FFmpeg did not start.");
            startedProcess.ErrorDataReceived += OnErrorDataReceived;
            startedProcess.BeginErrorReadLine();
            SetBackgroundPriority(startedProcess);

            return startedProcess;
        }

        void FinishGif()
        {
            RunProcess(
                "GIF palette generation",
                new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
                    "-i", outputPath.IntermediatePath,
                    "-vf", "palettegen=stats_mode=full:reserve_transparent=0",
                    outputPath.PalettePath,
                }
            );
            RunProcess(
                "GIF palette application",
                new[]
                {
                    "-hide_banner", "-nostdin", "-loglevel", "warning", "-y",
                    "-i", outputPath.IntermediatePath,
                    "-i", outputPath.PalettePath,
                    "-lavfi", "paletteuse=dither=sierra2_4a",
                    "-loop", "0",
                    outputPath.PartialPath,
                }
            );
            CompleteOutput(outputPath.PartialPath);
            outputPath.DeleteTemporaryFiles();
        }

        void OnErrorDataReceived(object sender, DataReceivedEventArgs eventArgs)
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data))
                return;

            lock (diagnosticGate)
            {
                var remaining = MaximumDiagnosticCharacters - diagnostic.Length;
                if (remaining <= 0)
                    return;

                if (eventArgs.Data.Length >= remaining)
                    diagnostic.Append(eventArgs.Data, 0, remaining);
                else
                    diagnostic.AppendLine(eventArgs.Data);
            }
        }

        string GetDiagnostic()
        {
            lock (diagnosticGate)
                return diagnostic.ToString();
        }

        void ClearDiagnostic()
        {
            lock (diagnosticGate)
                diagnostic.Clear();
        }

        void RunProcess(string operation, IReadOnlyList<string> arguments)
        {
            using var child = Process.Start(CreateStartInfo(arguments, redirectInput: false))
                              ?? throw new InvalidOperationException($"FFmpeg did not start for {operation}.");
            ClearDiagnostic();
            child.ErrorDataReceived += OnErrorDataReceived;
            child.BeginErrorReadLine();
            SetBackgroundPriority(child);
            if (!TrySetProcess(child))
            {
                try { child.Kill(); }
                catch { }

                throw new InvalidOperationException("FFmpeg encoding was aborted.");
            }

            try
            {
                WaitForSuccessfulExit(child, operation);
            }
            finally
            {
                ClearProcess(child);
            }
        }

        Process GetRunningProcess()
        {
            lock (processGate)
            {
                if (aborted)
                    throw new InvalidOperationException("FFmpeg encoding was aborted.");

                return process
                       ?? throw new InvalidOperationException("FFmpeg is no longer running.");
            }
        }

        bool TrySetProcess(Process value)
        {
            lock (processGate)
            {
                if (aborted || finished)
                    return false;

                process = value;
                return true;
            }
        }

        void ClearProcess(Process value)
        {
            lock (processGate)
                if (ReferenceEquals(process, value))
                    process = null;
        }

        void CompleteOutput(string source)
        {
            lock (processGate)
            {
                if (aborted)
                    throw new InvalidOperationException("FFmpeg encoding was aborted.");

                MoveCompletedFile(source, outputPath.AbsolutePath);
                finished = true;
            }
        }

        static void SetBackgroundPriority(Process value)
        {
            try { value.PriorityClass = ProcessPriorityClass.BelowNormal; } // protect editor responsiveness under software encoding load
            catch { }
        }

        static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments, bool redirectInput)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = FfmpegExecutable.Path,
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            return startInfo;
        }

        void WaitForSuccessfulExit(Process process, string operation)
        {
            if (!process.HasExited && !process.WaitForExit(ProcessTimeoutMilliseconds))
            {
                try { process.Kill(); }
                catch { }

                throw new TimeoutException($"FFmpeg timed out during {operation}.");
            }

            process.WaitForExit();
            if (process.ExitCode == 0)
                return;

            var diagnostic = GetDiagnostic();
            var detail = string.IsNullOrWhiteSpace(diagnostic)
                ? "No FFmpeg diagnostic was produced."
                : diagnostic.Trim();
            throw new InvalidOperationException(
                $"FFmpeg failed during {operation} with exit code {process.ExitCode}: {detail}"
            );
        }

        static void MoveCompletedFile(string source, string destination)
        {
            if (!File.Exists(source) || new FileInfo(source).Length == 0)
                throw new InvalidOperationException("FFmpeg completed without producing a video file.");

            File.Move(source, destination);
        }

    }

    static class FfmpegEncoderSelector
    {
        static readonly object cacheGate = new();
        static readonly Dictionary<string, ProbeResult> probeCache = new(StringComparer.Ordinal);

        public static FfmpegEncoderSpec Select(string format, int crf)
        {
            var candidates = BuildCandidates(format, crf);
            using var pooledDiagnostic = ConduitUtility.GetStringBuilder(out var diagnostic);
            foreach (var candidate in candidates)
            {
                var probe = Probe(candidate);
                if (probe.Supported)
                    return candidate;

                diagnostic.Append(candidate.DisplayName)
                    .Append(": ")
                    .AppendLine(probe.Diagnostic);
            }

            var detail = diagnostic.ToString().Trim();
            throw new InvalidOperationException(
                $"No working FFmpeg encoder was found for format '{format}'. {detail}"
            );
        }

        static IReadOnlyList<FfmpegEncoderSpec> BuildCandidates(string format, int crf)
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

        static ProbeResult Probe(FfmpegEncoderSpec spec)
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
                        "-i", "color=c=black:s=64x64:r=1:d=1",
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

        readonly struct ProbeResult
        {
            public ProbeResult(bool supported, string diagnostic, bool cacheable)
            {
                Supported = supported;
                Diagnostic = diagnostic;
                Cacheable = cacheable;
            }

            public bool Supported { get; }
            public string Diagnostic { get; }
            public bool Cacheable { get; }
        }
    }

    sealed class FfmpegEncoderSpec
    {
        public FfmpegEncoderSpec(
            string encoderName,
            string displayName,
            string inputFilter,
            string[] preInputArguments,
            string[] codecArguments,
            bool isGif)
        {
            EncoderName = encoderName;
            DisplayName = displayName;
            InputFilter = inputFilter;
            PreInputArguments = preInputArguments;
            CodecArguments = codecArguments;
            IsGif = isGif;
            ProbeFilter = inputFilter.Replace("vflip,", string.Empty);
            ProbeKey = encoderName
                       + "\n" + inputFilter
                       + "\n" + string.Join("\n", preInputArguments)
                       + "\n" + string.Join("\n", codecArguments);
        }

        public string EncoderName { get; }
        public string DisplayName { get; }
        public string InputFilter { get; }
        public string ProbeFilter { get; }
        public string[] PreInputArguments { get; }
        public string[] CodecArguments { get; }
        public bool IsGif { get; }
        public string ProbeKey { get; }
    }

    static class FfmpegExecutable
    {
        static readonly object pathGate = new();
        static string? resolvedPath;

        public static string Path
        {
            get
            {
                lock (pathGate)
                    return resolvedPath ??= Resolve();
            }
        }

        static string Resolve()
        {
            var configured = Environment.GetEnvironmentVariable("CONDUIT_FFMPEG_PATH");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (File.Exists(configured))
                    return configured;

                throw new InvalidOperationException(
                    $"CONDUIT_FFMPEG_PATH points to a missing file: {configured}"
                );
            }

            var executableName = Application.platform == RuntimePlatform.WindowsEditor
                ? "ffmpeg.exe"
                : "ffmpeg";
            var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in searchPath.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                var candidate = System.IO.Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                    return candidate;
            }

            var standardPaths = Application.platform switch
            {
                RuntimePlatform.OSXEditor => new[]
                {
                    "/opt/homebrew/bin/ffmpeg",
                    "/usr/local/bin/ffmpeg",
                    "/usr/bin/ffmpeg",
                },
                RuntimePlatform.LinuxEditor => new[]
                {
                    "/usr/local/bin/ffmpeg",
                    "/usr/bin/ffmpeg",
                    "/run/current-system/sw/bin/ffmpeg",
                },
                _ => Array.Empty<string>(),
            };
            foreach (var candidate in standardPaths)
                if (File.Exists(candidate))
                    return candidate;

            throw new InvalidOperationException(
                "FFmpeg was not found. Install it on PATH or set CONDUIT_FFMPEG_PATH to its executable."
            );
        }
    }
}
