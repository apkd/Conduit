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
    sealed partial class RecordingSession
    {
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
                SessionState.SetString(RecordTool.ReloadCompletionStateKey, completedMessage);
                Dispose();
                return;
            }

            Abort("Recording was interrupted by a Unity domain reload.");
            writerThread.Join();
            var message = writerCompletion.Task.Status == TaskStatus.RanToCompletion
                ? BuildSuccessMessage()
                : $"Previous recording failed for {outputPath.RelativePath}: Unity reloaded scripts before capture completed.";
            SessionState.SetString(RecordTool.ReloadCompletionStateKey, message);
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

    }
}
