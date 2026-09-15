#nullable enable

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Conduit.Runtime
{
    // mono's FileStream cannot interrupt Wine's synchronous FIFO poll during shutdown
    // use an overlapped handle and alertable waits so Wine can complete and cancel the read.
    sealed class RuntimeWineFifoStream : Stream
    {
        const int ErrorIoPending = 997;
        const uint WaitIoCompletion = 192;
        readonly object readGate = new();
        readonly SafeFileHandle handle;
        readonly ManualResetEvent stopped = new(false);
        readonly ManualResetEvent completed = new(false);
        readonly IntPtr[] waitHandles;
        int disposed;

        internal RuntimeWineFifoStream(string path)
        {
            const uint genericRead = 0x80000000;
            const uint shareReadWrite = 3;
            const uint openExisting = 3;
            const uint overlapped = 0x40000000;
            handle = CreateFileW(path, genericRead, shareReadWrite, IntPtr.Zero, openExisting, overlapped, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                stopped.Dispose();
                completed.Dispose();
                throw new IOException("Could not open the player FIFO.", new Win32Exception(error));
            }
            waitHandles = new[]
            {
                stopped.SafeWaitHandle.DangerousGetHandle(),
                completed.SafeWaitHandle.DangerousGetHandle(),
            };
        }

        public override bool CanRead => Volatile.Read(ref disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));

            lock (readGate)
            {
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(RuntimeWineFifoStream));
                if (count == 0)
                    return 0;

                completed.Reset();
                var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Overlapped>());
                var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                var pending = false;
                try
                {
                    Marshal.StructureToPtr(new Overlapped { Event = waitHandles[1] }, overlapped, false);
                    if (!ReadFile(handle, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, out var read, overlapped))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorIoPending)
                            return ReadError(error);

                        pending = true;
                        uint wait;
                        do
                        {
                            wait = WaitForMultipleObjectsEx(2, waitHandles, false, uint.MaxValue, true);
                        }
                        while (wait == WaitIoCompletion);

                        if (wait == 0)
                            throw new ObjectDisposedException(nameof(RuntimeWineFifoStream));
                        if (wait != 1)
                            throw new IOException("Could not wait for the player FIFO.", new Win32Exception());
                        pending = false;
                    }

                    return GetOverlappedResult(handle, overlapped, out read, false)
                        ? read
                        : ReadError(Marshal.GetLastWin32Error());
                }
                finally
                {
                    if (pending)
                    {
                        CancelIoEx(handle, overlapped);
                        // cancellation also completes asynchronously; retain the buffer until Wine finishes with it
                        while (WaitForSingleObjectEx(waitHandles[1], uint.MaxValue, true) == WaitIoCompletion) { }
                    }
                    pinned.Free();
                    Marshal.FreeHGlobal(overlapped);
                }
            }

            static int ReadError(int error) => error is 38 or 109
                ? 0
                : throw new IOException("Could not read the player FIFO.", new Win32Exception(error));
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || Interlocked.Exchange(ref disposed, 1) != 0)
                return;

            stopped.Set();
            lock (readGate)
            {
                handle.Dispose();
                completed.Dispose();
                stopped.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        [StructLayout(LayoutKind.Sequential)]
        struct Overlapped
        {
            internal IntPtr Status;
            internal IntPtr Count;
            internal uint Offset;
            internal uint OffsetHigh;
            internal IntPtr Event;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(
            string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out int read, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForMultipleObjectsEx(uint count, IntPtr[] handles, bool waitAll, uint timeout, bool alertable);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObjectEx(IntPtr handle, uint timeout, bool alertable);
    }
}
