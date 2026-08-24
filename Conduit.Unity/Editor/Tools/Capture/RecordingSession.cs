#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Conduit
{
    sealed partial class RecordingSession : IDisposable
    {
        const int SlotCount = 4; // overlaps readback, pipe writing, retained-frame reuse, and the next capture

        readonly object gate = new();
        readonly RecordSettings settings;
        readonly EditorCaptureSource source;
        readonly RecordOutputPath outputPath;
        readonly RenderTexture staging;
        readonly CaptureSlot[] slots;
        readonly FfmpegVideoEncoder encoder;
        readonly Thread writerThread;
        readonly TaskCompletionSource<RecordCompletion> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        readonly TaskCompletionSource<bool> writerCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        readonly Action<RecordingSession, RecordCompletion> onCompleted;

        TaskCompletionSource<bool>? waitCancellation;
        double startedAt;
        long scheduledFrameCount;
        long pendingFrameCount;
        long issuedSequenceCount;
        long droppedFrameCount;
        int lastGameFrame;
        int trailingRepeatCount;
        float previousCaptureDeltaTime;
        bool ownsCaptureDeltaTime;
        bool captureEnded;
        bool abortRequested;
        bool completing;
        bool disposed;
        string? abortDiagnostic;
        string? lastCaptureDiagnostic;

        RecordingSession(
            RecordSettings settings,
            EditorCaptureSource source,
            RecordOutputPath outputPath,
            RenderTexture staging,
            CaptureSlot[] slots,
            FfmpegVideoEncoder encoder,
            Action<RecordingSession, RecordCompletion> onCompleted)
        {
            this.settings = settings;
            this.source = source;
            this.outputPath = outputPath;
            this.staging = staging;
            this.slots = slots;
            this.encoder = encoder;
            this.onCompleted = onCompleted;
            writerThread = new(WriteFrames)
            {
                IsBackground = true,
                Name = "Conduit FFmpeg writer",
            };
        }

        internal bool ResultClaimed { get; private set; }

        internal string StartedMessage =>
            $"Recording started: {outputPath.RelativePath}\n"
            + $"Target: {settings.Target}; {staging.width}x{staging.height} at {settings.FrameRate} fps; encoder: {encoder.EncoderName}.\n"
            + "Call `record` again to wait for completion, or perform editor actions before waiting.";

        internal static async Task<RecordingSession> CreateAsync(
            RecordSettings settings,
            Action<RecordingSession, RecordCompletion> onCompleted)
        {
            ValidateAdjustedTime(settings);
            if (!SystemInfo.supportsAsyncGPUReadback)
                throw new InvalidOperationException(
                    "Recording requires a graphics device with asynchronous GPU readback support."
                );

            var source = await EditorCaptureSource.CreateAsync(settings.Target);
            FfmpegVideoEncoder? encoder = null;
            RenderTexture? staging = null;
            CaptureSlot[]? slots = null;
            try
            {
                var width = ScaleEven(source.Width, settings.ResolutionScale);
                var height = ScaleEven(source.Height, settings.ResolutionScale);
                var outputPath = RecordOutputPath.Allocate(
                    ConduitAssetPathUtility.GetProjectRootPath(),
                    settings.Format
                );
                encoder = FfmpegVideoEncoder.Start(settings, outputPath, width, height);
                staging = GpuCapture.CreateStagingTexture(width, height);
                slots = CreateSlots(checked(width * height * 4));
                var session = new RecordingSession(
                    settings,
                    source,
                    outputPath,
                    staging,
                    slots,
                    encoder,
                    onCompleted
                );
                foreach (var slot in slots)
                    slot.Owner = session;

                return session;
            }
            catch
            {
                if (slots != null)
                    foreach (var slot in slots)
                        slot.Dispose();

                if (staging != null)
                {
                    staging.Release();
                    Object.DestroyImmediate(staging);
                }

                encoder?.Dispose();
                source.Dispose();
                throw;
            }
        }

        internal void Start()
        {
            if (settings.AdjustDeltaTime)
            {
                previousCaptureDeltaTime = Time.captureDeltaTime;
                // wait for a newly rendered game frame instead of sampling the partial frame in which capture starts
                Time.captureDeltaTime = 1f / settings.FrameRate + 1e-7f;
                ownsCaptureDeltaTime = true;
                lastGameFrame = Time.frameCount;
            }

            startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnEditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            writerThread.Start();
            try
            {
                if (!settings.AdjustDeltaTime)
                    TickRealtime(startedAt);
            }
            catch (Exception exception)
            {
                Abort(exception.Message);
                writerThread.Join();
                throw;
            }
        }

        internal async Task<string> WaitAsync()
        {
            ResultClaimed = true;
            var cancellation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            waitCancellation = cancellation;
            try
            {
                var completedTask = await Task.WhenAny(completion.Task, cancellation.Task);
                if (completion.Task.IsCompleted)
                    return UnwrapCompletion(await completion.Task);

                if (completedTask == cancellation.Task)
                {
                    ResultClaimed = false;
                    throw new RecordTool.WaitCancelledException();
                }

                return UnwrapCompletion(await completion.Task);
            }
            finally
            {
                if (ReferenceEquals(waitCancellation, cancellation))
                    waitCancellation = null;
            }
        }

        internal bool CancelWait()
            => waitCancellation?.TrySetResult(true) == true;

        internal string BuildStatusLine()
        {
            var elapsed = settings.AdjustDeltaTime
                ? Math.Min(settings.DurationSeconds, (double)scheduledFrameCount / settings.FrameRate)
                : Math.Min(settings.DurationSeconds, EditorApplication.timeSinceStartup - startedAt);
            var elapsedText = elapsed.ToString("0.0", CultureInfo.InvariantCulture);
            var durationText = settings.DurationSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            var phase = captureEnded
                ? "encoding"
                : $"{elapsedText}/{durationText}s";
            return $"Recording: {settings.Target} -> {outputPath.RelativePath} ({phase}, {encoder.EncoderName})";
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Unsubscribe();
            RestoreCaptureDeltaTime();
            encoder.Dispose();
            source.Dispose();
            staging.Release();
            Object.DestroyImmediate(staging);
            foreach (var slot in slots)
                slot.Dispose();
        }

        void OnEditorUpdate()
        {
            if (completing)
                return;

            if (writerCompletion.Task.IsCompleted)
            {
                CompleteOnMainThread();
                return;
            }

            if (captureEnded)
                return;

            try
            {
                if (settings.AdjustDeltaTime)
                    TickAdjustedTime();
                else
                    TickRealtime(EditorApplication.timeSinceStartup);
            }
            catch (Exception exception)
            {
                Abort(exception.Message);
            }
        }

        void TickAdjustedTime()
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                Abort("Adjusted-time recording requires unpaused Play Mode for its entire duration.");
                return;
            }

            var gameFrame = Time.frameCount;
            if (gameFrame == lastGameFrame)
                return;

            if (gameFrame != lastGameFrame + 1)
            {
                Abort("Unity advanced more than one game frame between adjusted-time captures.");
                return;
            }

            lastGameFrame = gameFrame;
            var slot = AcquireSlot(block: true);
            if (slot == null)
                throw new InvalidOperationException("The FFmpeg writer stopped before adjusted-time capture completed.");

            if (!source.TryCapture(staging, out var diagnostic))
            {
                ReleaseUnusedSlot(slot);
                Abort(diagnostic);
                return;
            }

            IssueReadback(slot, repeatPrevious: 0);
            scheduledFrameCount++;
            if (scheduledFrameCount == 1)
                Time.captureDeltaTime = 1f / settings.FrameRate;

            if (scheduledFrameCount >= settings.FrameCount)
                EndCapture(0);
        }

        void TickRealtime(double now)
        {
            var elapsed = Math.Max(0d, now - startedAt);
            var dueFrameCount = Math.Min(
                settings.FrameCount,
                checked((long)Math.Floor(elapsed * settings.FrameRate) + 1L)
            );
            if (dueFrameCount > scheduledFrameCount)
            {
                pendingFrameCount += dueFrameCount - scheduledFrameCount;
                scheduledFrameCount = dueFrameCount;
            }

            if (pendingFrameCount > 0)
            {
                if (!source.IsValid)
                {
                    Abort($"The recording target '{settings.Target}' was closed during capture.");
                    return;
                }

                var slot = AcquireSlot(block: false);
                if (slot != null)
                {
                    if (source.TryCapture(staging, out var diagnostic))
                    {
                        // real-time capture samples only the newest image; ffmpeg repeats the
                        // previous image for missed deadlines without ever stalling the editor.
                        var repeatPrevious = checked((int)Math.Min(int.MaxValue, pendingFrameCount - 1));
                        IssueReadback(slot, repeatPrevious);
                        droppedFrameCount += repeatPrevious;
                        pendingFrameCount = 0;
                    }
                    else
                    {
                        lastCaptureDiagnostic = diagnostic;
                        ReleaseUnusedSlot(slot);
                    }
                }
            }

            if (scheduledFrameCount < settings.FrameCount)
                return;

            var trailing = checked((int)Math.Min(int.MaxValue, pendingFrameCount));
            droppedFrameCount += trailing;
            pendingFrameCount = 0;
            EndCapture(trailing);
        }

    }
}
