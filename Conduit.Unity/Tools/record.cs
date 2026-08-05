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
    /// <summary>Coordinates recording sessions across asynchronous tool calls and domain reloads.</summary>
    static class RecordTool
    {
        const string ReloadCompletionStateKey = "Conduit.Record.ReloadCompletion";

        static RecordingSession? active;
        static string? completedUncollected;

        public static async Task<string> ExecuteAsync(string? target, string[] args)
        {
            RestoreReloadCompletion();
            if (active != null)
                return await active.WaitAsync();

            var settings = RecordSettings.Parse(target, args);
            var previousCompletion = completedUncollected;
            RecordingSession? session = null;
            try
            {
                session = await RecordingSession.CreateAsync(settings, OnCompleted);
                active = session;
                session.Start();
            }
            catch (Exception exception)
            {
                active = null;
                session?.Dispose();
                if (previousCompletion != null)
                {
                    completedUncollected = null;
                    SessionState.EraseString(ReloadCompletionStateKey);
                    throw new InvalidOperationException(
                        $"{previousCompletion}\n\nA new recording did not start: {exception.Message}",
                        exception
                    );
                }

                throw;
            }

            completedUncollected = null;
            SessionState.EraseString(ReloadCompletionStateKey);
            var startedSession = session
                                 ?? throw new InvalidOperationException("Recording initialization did not complete.");
            return previousCompletion == null
                ? startedSession.StartedMessage
                : $"{previousCompletion}\n\n{startedSession.StartedMessage}";
        }

        public static bool CancelWait() => active?.CancelWait() == true;

        public static string? BuildStatusLine()
        {
            RestoreReloadCompletion();
            return active?.BuildStatusLine();
        }

        static void OnCompleted(RecordingSession session, RecordCompletion completion)
        {
            if (ReferenceEquals(active, session))
                active = null;

            if (!session.ResultClaimed)
            {
                completedUncollected = completion.Message;
                SessionState.SetString(ReloadCompletionStateKey, completion.Message);
            }
        }

        static void RestoreReloadCompletion()
        {
            if (completedUncollected != null)
                return;

            var restored = SessionState.GetString(ReloadCompletionStateKey, string.Empty);
            if (restored.Length == 0)
                return;

            completedUncollected = restored;
        }

        internal sealed class WaitCancelledException : OperationCanceledException { }

        sealed class RecordingSession : IDisposable
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

            public bool ResultClaimed { get; private set; }

            public string StartedMessage =>
                $"Recording started: {outputPath.RelativePath}\n"
                + $"Target: {settings.Target}; {staging.width}x{staging.height} at {settings.FrameRate} fps; encoder: {encoder.EncoderName}.\n"
                + "Call `record` again to wait for completion, or perform editor actions before waiting.";

            public static async Task<RecordingSession> CreateAsync(
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

            public void Start()
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

            public async Task<string> WaitAsync()
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
                        throw new WaitCancelledException();
                    }

                    return UnwrapCompletion(await completion.Task);
                }
                finally
                {
                    if (ReferenceEquals(waitCancellation, cancellation))
                        waitCancellation = null;
                }
            }

            public bool CancelWait()
                => waitCancellation?.TrySetResult(true) == true;

            public string BuildStatusLine()
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

            CaptureSlot? AcquireSlot(bool block)
            {
                while (true)
                {
                    CaptureSlot? pending = null;
                    lock (gate)
                    {
                        foreach (var slot in slots)
                            if (slot.State == SlotState.Free)
                            {
                                slot.State = SlotState.Reserved;
                                return slot;
                            }

                        if (!block || abortRequested || writerCompletion.Task.IsCompleted)
                            return null;

                        foreach (var slot in slots)
                            if (slot.State == SlotState.Pending)
                            {
                                pending = slot;
                                break;
                            }

                        if (pending == null)
                        {
                            Monitor.Wait(gate, 10);
                            continue;
                        }
                    }

                    var request = pending.Request;
                    request.WaitForCompletion();
                    CompleteReadback(pending, request);
                }
            }

            void IssueReadback(CaptureSlot slot, int repeatPrevious)
            {
                lock (gate)
                {
                    slot.Sequence = issuedSequenceCount++;
                    slot.RepeatPrevious = repeatPrevious;
                    slot.Error = null;
                    slot.State = SlotState.Pending;
                }

                try
                {
                    slot.Request = AsyncGPUReadback.RequestIntoNativeArray(
                        ref slot.Buffer,
                        staging,
                        0,
                        TextureFormat.RGBA32,
                        slot.Callback
                    );
                }
                catch
                {
                    lock (gate)
                    {
                        issuedSequenceCount--;
                        slot.State = SlotState.Free;
                        Monitor.PulseAll(gate);
                    }

                    throw;
                }
            }

            void CompleteReadback(CaptureSlot slot, AsyncGPUReadbackRequest request)
            {
                lock (gate)
                {
                    if (slot.State != SlotState.Pending)
                        return;

                    slot.Error = request.hasError
                        ? "Unity reported an asynchronous GPU readback error."
                        : null;
                    slot.State = SlotState.Ready;
                    Monitor.PulseAll(gate);
                }
            }

            void ReleaseUnusedSlot(CaptureSlot slot)
            {
                lock (gate)
                {
                    slot.State = SlotState.Free;
                    Monitor.PulseAll(gate);
                }
            }

            void EndCapture(int trailingRepeats)
            {
                if (captureEnded)
                    return;

                captureEnded = true;
                trailingRepeatCount = trailingRepeats;
                RestoreCaptureDeltaTime();
                lock (gate)
                    Monitor.PulseAll(gate);
            }

            void Abort(string diagnostic)
            {
                if (abortRequested)
                    return;

                abortRequested = true;
                abortDiagnostic = diagnostic;
                captureEnded = true;
                RestoreCaptureDeltaTime();
                CompletePendingReadbacks();
                lock (gate)
                    Monitor.PulseAll(gate);

                encoder.Abort();
            }

            void CompletePendingReadbacks()
            {
                foreach (var slot in slots)
                {
                    if (slot.State != SlotState.Pending)
                        continue;

                    var request = slot.Request;
                    request.WaitForCompletion();
                    CompleteReadback(slot, request);
                }
            }

            void WriteFrames()
            {
                CaptureSlot? retained = null;
                long expectedSequence = 0;
                long writtenFrameCount = 0;
                try
                {
                    while (true)
                    {
                        CaptureSlot? current = null;
                        lock (gate)
                        {
                            while (current == null)
                            {
                                if (abortRequested)
                                    throw new InvalidOperationException(
                                        abortDiagnostic ?? "Recording was aborted."
                                    );

                                foreach (var slot in slots)
                                    if (slot.State == SlotState.Ready
                                        && slot.Sequence == expectedSequence)
                                    {
                                        current = slot;
                                        current.State = SlotState.Writing;
                                        break;
                                    }

                                if (current != null)
                                    break;

                                if (captureEnded && expectedSequence >= issuedSequenceCount)
                                    break;

                                Monitor.Wait(gate);
                            }
                        }

                        if (current == null)
                            break;

                        if (current.Error != null)
                            throw new InvalidOperationException(current.Error);

                        var repeatedFrame = retained ?? current;
                        for (var repeat = 0; repeat < current.RepeatPrevious; repeat++)
                        {
                            encoder.WriteFrame(repeatedFrame.Buffer);
                            writtenFrameCount++;
                        }

                        encoder.WriteFrame(current.Buffer);
                        writtenFrameCount++;

                        lock (gate)
                        {
                            if (retained != null)
                                retained.State = SlotState.Free;

                            // retain one image so missed real-time deadlines can be filled without copying pixels
                            current.State = SlotState.Retained;
                            retained = current;
                            expectedSequence++;
                            Monitor.PulseAll(gate);
                        }
                    }

                    if (retained == null)
                        throw new InvalidOperationException(
                            lastCaptureDiagnostic == null
                                ? "No video frame was captured."
                                : $"No video frame was captured: {lastCaptureDiagnostic}"
                        );

                    for (var repeat = 0; repeat < trailingRepeatCount; repeat++)
                    {
                        encoder.WriteFrame(retained.Buffer);
                        writtenFrameCount++;
                    }

                    if (writtenFrameCount != settings.FrameCount)
                        throw new InvalidOperationException(
                            $"Video frame accounting failed: wrote {writtenFrameCount} of {settings.FrameCount} frames."
                        );

                    encoder.Finish();
                    writerCompletion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    encoder.Abort();
                    writerCompletion.TrySetException(exception);
                }
                finally
                {
                    lock (gate)
                    {
                        if (retained != null)
                            retained.State = SlotState.Free;

                        Monitor.PulseAll(gate);
                    }
                }
            }

            void CompleteOnMainThread()
            {
                if (completing)
                    return;

                completing = true;
                RestoreCaptureDeltaTime();
                Unsubscribe();
                var exception = writerCompletion.Task.Exception?.GetBaseException();
                if (exception != null && !abortRequested)
                    Abort(exception.Message); // completes any GPU requests the failed writer left behind

                writerThread.Join();
                var result = exception == null
                    ? RecordCompletion.Success(BuildSuccessMessage())
                    : RecordCompletion.Failure(
                        $"Recording failed for {outputPath.RelativePath}: {exception.Message}"
                    );

                Dispose();
                completion.TrySetResult(result);
                onCompleted(this, result);
            }

            void OnBeforeAssemblyReload()
            {
                if (writerCompletion.Task.IsCompleted)
                {
                    var exception = writerCompletion.Task.Exception?.GetBaseException();
                    if (exception != null && !abortRequested)
                        Abort(exception.Message);

                    writerThread.Join();
                    var completedMessage = exception == null
                        ? BuildSuccessMessage()
                        : $"Previous recording failed for {outputPath.RelativePath}: {exception.Message}";
                    SessionState.SetString(ReloadCompletionStateKey, completedMessage);
                    Dispose();
                    return;
                }

                Abort("Recording was interrupted by a Unity domain reload.");
                writerThread.Join();
                var message = writerCompletion.Task.Status == TaskStatus.RanToCompletion
                    ? BuildSuccessMessage()
                    : $"Previous recording failed for {outputPath.RelativePath}: Unity reloaded scripts before capture completed.";
                SessionState.SetString(ReloadCompletionStateKey, message);
                Dispose();
            }

            void OnEditorQuitting()
            {
                Abort("Recording was interrupted because the Unity editor is quitting.");
                writerThread.Join();
                Dispose();
            }

            void RestoreCaptureDeltaTime()
            {
                if (!ownsCaptureDeltaTime)
                    return;

                Time.captureDeltaTime = previousCaptureDeltaTime;
                ownsCaptureDeltaTime = false;
            }

            void Unsubscribe()
            {
                EditorApplication.update -= OnEditorUpdate;
                EditorApplication.quitting -= OnEditorQuitting;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            }

            static string UnwrapCompletion(RecordCompletion result)
            {
                if (!result.Succeeded)
                    throw new InvalidOperationException(result.Message);

                return result.Message;
            }

            string BuildSuccessMessage()
                => $"Recording complete: {outputPath.RelativePath}\n"
                   + $"Encoder: {encoder.EncoderName}; frames: {settings.FrameCount}; held frames: {droppedFrameCount}.";

            static void ValidateAdjustedTime(RecordSettings settings)
            {
                if (!settings.AdjustDeltaTime)
                    return;

                if (!EditorApplication.isPlaying || EditorApplication.isPaused)
                    throw new InvalidOperationException(
                        "adjustDeltaTime=true requires unpaused Play Mode."
                    );

                if (Math.Abs(Time.captureDeltaTime) > float.Epsilon)
                    throw new InvalidOperationException(
                        $"Time.captureDeltaTime is already set to {Time.captureDeltaTime.ToString(CultureInfo.InvariantCulture)}."
                    );
            }

            static int ScaleEven(int value, float scale)
            {
                var scaled = Mathf.Max(2, Mathf.RoundToInt(value * scale));
                return scaled % 2 == 0 ? scaled : scaled - 1;
            }

            static CaptureSlot[] CreateSlots(int byteCount)
            {
                var slots = new CaptureSlot[SlotCount];
                try
                {
                    for (var index = 0; index < slots.Length; index++)
                        slots[index] = new(index, byteCount);

                    return slots;
                }
                catch
                {
                    foreach (var slot in slots)
                        slot?.Dispose();

                    throw;
                }
            }

            sealed class CaptureSlot : IDisposable
            {
                public CaptureSlot(int index, int byteCount)
                {
                    Buffer = new(byteCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    Callback = request => Owner?.CompleteSlot(index, request); // cached to keep capture allocation-free
                }

                public RecordingSession? Owner;
                public NativeArray<byte> Buffer;
                public AsyncGPUReadbackRequest Request;
                public readonly Action<AsyncGPUReadbackRequest> Callback;
                public SlotState State;
                public long Sequence;
                public int RepeatPrevious;
                public string? Error;

                public void Dispose()
                {
                    if (Buffer.IsCreated)
                        Buffer.Dispose();
                }

            }

            void CompleteSlot(int slotIndex, AsyncGPUReadbackRequest request)
            {
                if (slotIndex < 0 || slotIndex >= slots.Length)
                    return;

                CompleteReadback(slots[slotIndex], request);
            }

            enum SlotState : byte
            {
                Free,
                Reserved,
                Pending,
                Ready,
                Writing,
                Retained,
            }
        }
    }

    readonly struct RecordSettings
    {
        public RecordSettings(
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

        public string Target { get; }
        public double DurationSeconds { get; }
        public bool AdjustDeltaTime { get; }
        public int FrameRate { get; }
        public float ResolutionScale { get; }
        public string Format { get; }
        public int Crf { get; }
        public int FrameCount { get; }

        public static RecordSettings Parse(string? target, string[] args)
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

    readonly struct RecordCompletion
    {
        RecordCompletion(bool succeeded, string message)
        {
            Succeeded = succeeded;
            Message = message;
        }

        public bool Succeeded { get; }
        public string Message { get; }

        public static RecordCompletion Success(string message) => new(true, message);
        public static RecordCompletion Failure(string message) => new(false, message);
    }

    readonly struct RecordOutputPath
    {
        const string OutputDirectoryName = "Recordings";

        RecordOutputPath(string directory, int index, string extension)
        {
            var baseName = index.ToString(CultureInfo.InvariantCulture);
            AbsolutePath = Path.Combine(directory, baseName + extension);
            PartialPath = Path.Combine(directory, baseName + ".partial" + extension);
            IntermediatePath = Path.Combine(directory, baseName + ".recording.mkv");
            PalettePath = Path.Combine(directory, baseName + ".palette.png");
            RelativePath = $"Library/{OutputDirectoryName}/{baseName}{extension}";
        }

        public string AbsolutePath { get; }
        public string PartialPath { get; }
        public string IntermediatePath { get; }
        public string PalettePath { get; }
        public string RelativePath { get; }

        public static RecordOutputPath Allocate(string projectPath, string format)
        {
            var directory = Path.Combine(projectPath, "Library", OutputDirectoryName);
            Directory.CreateDirectory(directory);
            var extension = format switch
            {
                "gif"  => ".gif",
                "webm" => ".webm",
                _      => ".mp4",
            };

            for (var index = 0; index < int.MaxValue; index++)
            {
                var candidate = new RecordOutputPath(directory, index, extension);
                if (IsAvailable(index, directory))
                    return candidate;
            }

            throw new InvalidOperationException("Could not allocate a recording output path.");
        }

        public void DeleteTemporaryFiles()
        {
            TryDelete(PartialPath);
            TryDelete(IntermediatePath);
            TryDelete(PalettePath);
        }

        static bool IsAvailable(int index, string directory)
        {
            var baseName = index.ToString(CultureInfo.InvariantCulture);
            return !File.Exists(Path.Combine(directory, baseName + ".mp4"))
                   && !File.Exists(Path.Combine(directory, baseName + ".webm"))
                   && !File.Exists(Path.Combine(directory, baseName + ".gif"))
                   && !File.Exists(Path.Combine(directory, baseName + ".partial.mp4"))
                   && !File.Exists(Path.Combine(directory, baseName + ".partial.webm"))
                   && !File.Exists(Path.Combine(directory, baseName + ".partial.gif"))
                   && !File.Exists(Path.Combine(directory, baseName + ".recording.mkv"))
                   && !File.Exists(Path.Combine(directory, baseName + ".palette.png"));
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
    }
}
