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
            // static editor views may not dispatch another GPU callback after capture ends
            CompletePendingReadbacks();
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

        sealed class CaptureSlot : IDisposable
        {
            internal CaptureSlot(int index, int byteCount)
            {
                Buffer = new(byteCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Callback = request => Owner?.CompleteSlot(index, request); // cached to keep capture allocation-free
            }

            internal RecordingSession? Owner;
            internal NativeArray<byte> Buffer;
            internal AsyncGPUReadbackRequest Request;
            internal readonly Action<AsyncGPUReadbackRequest> Callback;
            internal SlotState State;
            internal long Sequence;
            internal int RepeatPrevious;
            internal string? Error;

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
