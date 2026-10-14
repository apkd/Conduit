using System.Runtime.InteropServices;
using System.Text;

namespace Conduit;

public sealed class FifoReadinessTests
{
    [Test]
    public async Task IdleReadinessHasNoPeriodicWakeups(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        var wait = readiness.WaitAsync(pipe.ReadDescriptor, timeout.Token);
        await UntilAsync(() => readiness.PendingCount == 1 && readiness.PollCalls >= 2, timeout.Token);
        var before = readiness.PollCalls;

        await Task.Delay(200, timeout.Token);

        await Assert.That(wait.IsCompleted).IsFalse();
        // allow an interrupted syscall while still detecting recurring retries.
        await Assert.That(readiness.PollCalls - before <= 1).IsTrue();
        await pipe.WriteAsync([1], timeout.Token);
        await wait;
        await Assert.That(readiness.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task AlreadyReadableDescriptorHasNoLostWakeup(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        await pipe.WriteAsync([1], timeout.Token);

        await readiness.WaitAsync(pipe.ReadDescriptor, timeout.Token);

        await Assert.That(readiness.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancellingOneWaitDoesNotCancelAnother(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var first = new TestPipe();
        using var second = new TestPipe();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var cancelled = readiness.WaitAsync(first.ReadDescriptor, cancellation.Token);
        var live = readiness.WaitAsync(second.ReadDescriptor, timeout.Token);

        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(cancelled);
        await Assert.That(live.IsCompleted).IsFalse();
        await Assert.That(readiness.PendingCount).IsEqualTo(1);
        await second.WriteAsync([1], timeout.Token);
        await live;
        await Assert.That(readiness.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task DisposingReadinessCompletesPendingWait(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        var pending = readiness.WaitAsync(pipe.ReadDescriptor, timeout.Token);

        readiness.Dispose();
        readiness.Dispose();

        await ExpectAsync<ObjectDisposedException>(pending);
        await Assert.That(readiness.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancelledReaderRetainsPartialUtf8Line(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        using var reader = pipe.TakeReader(readiness);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var bytes = Encoding.UTF8.GetBytes("🙂\n");
        await pipe.WriteAsync(bytes[..3], timeout.Token);
        var pending = reader.ReadLineAsync(cancellation.Token).AsTask();
        await UntilAsync(() => readiness.PendingCount == 1, timeout.Token);

        cancellation.Cancel();
        var exception = await ExpectAsync<OperationCanceledException>(pending);
        await Assert.That(exception.CancellationToken).IsEqualTo(cancellation.Token);
        await pipe.WriteAsync(bytes[3..], timeout.Token);

        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("🙂");
    }

    [Test]
    public async Task DisposingReaderUnblocksPendingRead(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        using var reader = pipe.TakeReader(readiness);
        var pending = reader.ReadLineAsync(timeout.Token).AsTask();
        await UntilAsync(() => readiness.PendingCount == 1, timeout.Token);

        reader.Dispose();
        reader.Dispose();

        await ExpectAsync<ObjectDisposedException>(pending);
        await Assert.That(readiness.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task ReaderHandlesCoalescedLinesCrLfAndUnterminatedEof(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        using var reader = pipe.TakeReader(readiness);
        await pipe.WriteAsync(Encoding.UTF8.GetBytes("first\r\n\nsecond\n尾"), timeout.Token);
        pipe.CloseWriter();

        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("first");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("second");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("尾");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsNull();
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsNull();
    }

    [Test]
    public async Task ReaderHandlesUtf8AcrossNativeReadBoundary(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        using var reader = pipe.TakeReader(readiness);
        var expected = new string('a', 4095) + "🙂";
        var pending = reader.ReadLineAsync(timeout.Token).AsTask();
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(expected + "\r\n"), timeout.Token);

        await Assert.That(await pending).IsEqualTo(expected);
    }

    [Test]
    public async Task ReaderHandlesLargeLinesAndFollowingMessages(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        using var reader = pipe.TakeReader(readiness);
        var expected = new string('x', 2 * 1024 * 1024) + "🙂";
        var pending = reader.ReadLineAsync(timeout.Token).AsTask();

        await pipe.WriteAsync(Encoding.UTF8.GetBytes(expected + "\r\nnext\n"), timeout.Token);
        pipe.CloseWriter();

        await Assert.That(await pending).IsEqualTo(expected);
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsEqualTo("next");
        await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsNull();
    }

    [Test]
    public async Task ConcurrentReadIsRejectedWithoutBreakingFirst(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var pipe = new TestPipe();
        using var reader = pipe.TakeReader(readiness);
        var first = reader.ReadLineAsync(timeout.Token).AsTask();

        await ExpectAsync<InvalidOperationException>(reader.ReadLineAsync(timeout.Token).AsTask());
        await pipe.WriteAsync(Encoding.UTF8.GetBytes("ok\n"), timeout.Token);

        await Assert.That(await first).IsEqualTo("ok");
    }

    [Test]
    public async Task RepeatedCancellationAndDescriptorChurnLeaveNoWaiters(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        using var otherPipe = new TestPipe();
        var unrelated = readiness.WaitAsync(otherPipe.ReadDescriptor, timeout.Token);
        for (var index = 0; index < 64; index++)
        {
            using var pipe = new TestPipe();
            using var reader = pipe.TakeReader(readiness);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var first = reader.ReadLineAsync(cancellation.Token).AsTask();
            cancellation.Cancel();
            await ExpectAsync<OperationCanceledException>(first);
            var next = reader.ReadLineAsync(timeout.Token).AsTask();
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("ok\n"), timeout.Token);
            await Assert.That(await next).IsEqualTo("ok");
            await Assert.That(unrelated.IsCompleted).IsFalse();
        }
        await Assert.That(readiness.PendingCount).IsEqualTo(1);
        await otherPipe.WriteAsync([1], timeout.Token);
        await unrelated;
        await Assert.That(readiness.PendingCount).IsEqualTo(0);
    }

    [Test]
    public async Task NamedFifoReaderReceivesDataAndPeerClose(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return;
        using var timeout = Deadline(ct);
        using var readiness = new LinuxFifoReadiness();
        var directory = Path.Combine(Path.GetTempPath(), "conduit-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "response.fifo");
        int keeper = -1;
        try
        {
            if (mkfifo(path, 0x180) != 0)
                throw NativeError();
            keeper = open(path, 2 | 0x800 | 0x80000); // O_RDWR | O_NONBLOCK | O_CLOEXEC
            if (keeper < 0)
                throw NativeError();
            using var reader = BridgeTransport.FifoLineReader.Open(path, readiness);
            var pending = reader.ReadLineAsync(timeout.Token).AsTask();
            var bytes = Encoding.UTF8.GetBytes("fifo\n");
            if (Write(keeper, bytes) != bytes.Length)
                throw NativeError();
            close(keeper);
            keeper = -1;
            await Assert.That(await pending).IsEqualTo("fifo");
            await Assert.That(await reader.ReadLineAsync(timeout.Token)).IsNull();
        }
        finally
        {
            if (keeper >= 0)
                close(keeper);
            Directory.Delete(directory, recursive: true);
        }
    }

    static CancellationTokenSource Deadline(CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(10));
        return source;
    }

    static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
            await Task.Delay(5, ct);
    }

    static async Task<T> ExpectAsync<T>(Task operation) where T : Exception
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    sealed class TestPipe : IDisposable
    {
        int reader;
        int writer;
        internal int ReadDescriptor => reader;

        internal TestPipe()
        {
            var descriptors = new int[2];
            if (pipe2(descriptors, 0x800 | 0x80000) != 0)
                throw NativeError();
            reader = descriptors[0];
            writer = descriptors[1];
        }

        internal BridgeTransport.FifoLineReader TakeReader(LinuxFifoReadiness readiness)
        {
            if (reader < 0)
                throw new InvalidOperationException("Reader ownership was already transferred.");
            var result = new BridgeTransport.FifoLineReader(reader, readiness);
            reader = -1;
            return result;
        }

        internal async Task WriteAsync(byte[] bytes, CancellationToken ct)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                var count = Write(writer, bytes.AsSpan(offset));
                if (count > 0)
                {
                    offset += checked((int)count);
                    continue;
                }
                var error = Marshal.GetLastPInvokeError();
                if (count < 0 && error == 4)
                    continue;
                if (count < 0 && error == 11)
                {
                    await Task.Delay(1, ct);
                    continue;
                }
                throw NativeError();
            }
        }

        internal void CloseWriter()
        {
            if (writer >= 0)
                close(writer);
            writer = -1;
        }

        public void Dispose()
        {
            if (reader >= 0)
                close(reader);
            reader = -1;
            CloseWriter();
        }
    }

    static IOException NativeError() => new($"Test FIFO syscall failed (errno {Marshal.GetLastPInvokeError()}).");

    static unsafe nint Write(int descriptor, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* buffer = bytes)
            return write(descriptor, buffer, (nuint)bytes.Length);
    }

    [DllImport("libc", SetLastError = true)]
    static extern int pipe2([Out] int[] descriptors, int flags);
    [DllImport("libc", SetLastError = true)]
    static extern int mkfifo(string path, uint mode);
    [DllImport("libc", SetLastError = true)]
    static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    static extern unsafe nint write(int descriptor, byte* bytes, nuint count);
    [DllImport("libc", SetLastError = true)]
    static extern int close(int descriptor);
}
