#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Collections;

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

        internal string EncoderName => spec.DisplayName;

        internal static FfmpegVideoEncoder Start(
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

        internal void WriteFrame(NativeArray<byte> frame)
        {
            var runningProcess = GetRunningProcess();
            runningProcess.StandardInput.BaseStream.Write(frame.AsReadOnlySpan());
        }

        internal void Finish()
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

        internal void Abort()
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
}
