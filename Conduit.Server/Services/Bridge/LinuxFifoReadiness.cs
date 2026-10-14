using System.Runtime.InteropServices;

namespace Conduit;

// one shared thread waits indefinitely for kernel readiness, leaving the thread pool available.
sealed class LinuxFifoReadiness : IDisposable
{
    const short Readable = 0x001;
    const short Invalid = 0x020;
    const int Interrupted = 4;
    const int Again = 11;
    static readonly Lazy<LinuxFifoReadiness> shared = new(() => new());
    readonly object gate = new();
    readonly Dictionary<TaskCompletionSource, int> waiters = new();
    readonly int wakeDescriptor;
    readonly Thread thread;
    bool stopping;
    Exception? failure;
    long pollCalls;

    // the shared instance lives until process exit; tests own separate instances.
    internal static LinuxFifoReadiness Shared => shared.Value;
    internal long PollCalls => Interlocked.Read(ref pollCalls);
    internal int PendingCount
    {
        get
        {
            lock (gate)
                return waiters.Count;
        }
    }

    internal LinuxFifoReadiness()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("FIFO readiness requires Linux.");

        const int nonBlocking = 0x800;
        const int closeOnExec = 0x80000;
        wakeDescriptor = eventfd(0, nonBlocking | closeOnExec);
        if (wakeDescriptor < 0)
            throw NativeError("create FIFO wake descriptor");

        thread = new Thread(Run) { IsBackground = true, Name = "Conduit FIFO readiness" };
        try
        {
            thread.Start();
        }
        catch
        {
            close(wakeDescriptor);
            throw;
        }
    }

    // the owner serializes read/close and cancels its wait before disposing the FIFO.
    // registration identities prevent old snapshots from completing waits on recycled descriptors.
    internal Task WaitAsync(int descriptor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (failure is { } exception)
                throw new IOException("The FIFO readiness thread stopped.", exception);
            ObjectDisposedException.ThrowIf(stopping, this);
            waiters.Add(ready, descriptor);
            try
            {
                Wake();
            }
            catch
            {
                waiters.Remove(ready);
                throw;
            }
        }

        return WaitAndRemoveAsync(ready, ct);
    }

    async Task WaitAndRemoveAsync(TaskCompletionSource ready, CancellationToken ct)
    {
        try
        {
            await ready.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
                if (waiters.Remove(ready))
                    Wake();
        }
    }

    void Run()
    {
        try
        {
            while (true)
            {
                KeyValuePair<TaskCompletionSource, int>[] snapshot;
                lock (gate)
                {
                    if (stopping)
                        return;
                    snapshot = waiters.ToArray();
                }

                var descriptors = new PollDescriptor[snapshot.Length + 1];
                descriptors[0] = new(wakeDescriptor);
                for (var index = 0; index < snapshot.Length; index++)
                    descriptors[index + 1] = new(snapshot[index].Value);

                Interlocked.Increment(ref pollCalls);
                if (poll(descriptors, (nuint)descriptors.Length, -1) < 0)
                {
                    if (Marshal.GetLastPInvokeError() == Interrupted)
                        continue;
                    throw NativeError("wait for FIFO readiness");
                }

                lock (gate)
                {
                    if (stopping)
                        return;
                    if (descriptors[0].ReturnedEvents != 0)
                        DrainWake();

                    for (var index = 0; index < snapshot.Length; index++)
                    {
                        var events = descriptors[index + 1].ReturnedEvents;
                        if (events == 0 || !waiters.Remove(snapshot[index].Key))
                            continue;

                        if ((events & Invalid) != 0)
                            snapshot[index].Key.TrySetException(new IOException("The FIFO descriptor was closed."));
                        else
                            // drain buffered bytes before observing EOF or the underlying read error.
                            snapshot[index].Key.TrySetResult();
                    }
                }
            }
        }
        catch (Exception exception)
        {
            lock (gate)
                failure = exception;
        }
        finally
        {
            lock (gate)
            {
                stopping = true;
                foreach (var ready in waiters.Keys)
                    ready.TrySetException(failure is { } exception
                        ? new IOException("The FIFO readiness thread stopped.", exception)
                        : new ObjectDisposedException(nameof(LinuxFifoReadiness)));
                waiters.Clear();
                close(wakeDescriptor);
            }
        }
    }

    // callers hold the gate so the wake descriptor cannot close concurrently.
    void Wake()
    {
        ulong value = 1;
        while (write(wakeDescriptor, ref value, (nuint)sizeof(ulong)) < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == Interrupted)
                continue;
            if (error == Again) // already signalled (counter full)
                return;
            throw new IOException($"Could not wake the FIFO readiness thread (errno {error}).");
        }
    }

    void DrainWake()
    {
        while (read(wakeDescriptor, out _, (nuint)sizeof(ulong)) < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == Interrupted)
                continue;
            if (error == Again)
                return;
            throw new IOException($"Could not drain the FIFO wake descriptor (errno {error}).");
        }
    }

    public void Dispose()
    {
        lock (gate)
            if (!stopping)
            {
                stopping = true;
                Wake();
            }
        thread.Join();
    }

    static IOException NativeError(string operation)
        => new($"Could not {operation} (errno {Marshal.GetLastPInvokeError()}).");

    [StructLayout(LayoutKind.Sequential)]
    struct PollDescriptor(int descriptor)
    {
        internal int Descriptor = descriptor;
        internal short Events = Readable;
        internal short ReturnedEvents = 0;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int eventfd(uint initialValue, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int poll([In, Out] PollDescriptor[] descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    static extern nint read(int descriptor, out ulong value, nuint count);

    [DllImport("libc", SetLastError = true)]
    static extern nint write(int descriptor, ref ulong value, nuint count);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int descriptor);
}
