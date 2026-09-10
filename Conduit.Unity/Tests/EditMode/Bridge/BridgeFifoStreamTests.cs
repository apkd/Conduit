#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;

[Platform("Linux")]
public sealed class BridgeFifoStreamTests
{
    [TestCase(1)]
    [TestCase(1023)]
    [TestCase(1024)]
    [TestCase(1500)]
    [TestCase(4095)]
    [TestCase(4096)]
    [TestCase(4097)]
    [TestCase(65536)]
    public async Task CompleteLineArrivesWithoutAnotherWrite(int length)
    {
        using var fifo = new Fifo();
        var expected = new string('x', length);
        var read = fifo.Reader.ReadLineAsync();
        try
        {
            await fifo.SendAsync(expected + "\n");
            await AssertLineAsync(read, expected);
        }
        finally
        {
            // closing the writer releases a failed read without leaving a blocked worker behind
            fifo.Writer.Dispose();
            await read;
        }
    }

    [Test]
    public async Task CoalescedLinesArriveWithoutAnotherWrite()
    {
        using var fifo = new Fifo();
        var expected = new[] { "first", new string('x', 1500), "last" };
        Task<string?>? read = null;
        try
        {
            await fifo.SendAsync(string.Join("\n", expected) + "\n");
            foreach (var line in expected)
            {
                read = fifo.Reader.ReadLineAsync();
                await AssertLineAsync(read, line);
            }
        }
        finally
        {
            fifo.Writer.Dispose();
            if (read != null)
                await read;
        }
    }

    [Test]
    public async Task SynchronousReaderCompletesWithoutAnotherWrite()
    {
        using var fifo = new Fifo(asynchronous: false);
        var expected = new string('x', 1500);
        var read = Task.Run(() => fifo.Reader.ReadLine());
        try
        {
            await fifo.SendAsync(expected + "\n");
            await AssertLineAsync(read, expected);
        }
        finally
        {
            fifo.Writer.Dispose();
            await read;
        }
    }

    [Test]
    public async Task FragmentedUtf8LineArrivesWithoutAnotherWrite()
    {
        using var fifo = new Fifo();
        var expected = new string('x', 1023) + "🌍 café";
        var bytes = Encoding.UTF8.GetBytes(expected + "\r\n");
        var read = fifo.Reader.ReadLineAsync();
        try
        {
            // split a multibyte character across both writes and text-reader buffers
            await fifo.Writer.WriteAsync(bytes, 0, 1025);
            await fifo.Writer.FlushAsync();
            Assert.That(read.IsCompleted, Is.False);
            await fifo.Writer.WriteAsync(bytes, 1025, bytes.Length - 1025);
            await fifo.Writer.FlushAsync();
            await AssertLineAsync(read, expected);
        }
        finally
        {
            fifo.Writer.Dispose();
            await read;
        }
    }

    [TestCase(0)]
    [TestCase(1500)]
    public async Task PeerCloseCompletesPendingRead(int length)
    {
        using var fifo = new Fifo();
        var expected = new string('x', length);
        var read = fifo.Reader.ReadLineAsync();
        await fifo.SendAsync(expected);
        fifo.Writer.Dispose();
        await AssertLineAsync(read, length == 0 ? null : expected);
        Assert.That(await fifo.Reader.ReadLineAsync(), Is.Null);
    }

    static async Task AssertLineAsync(Task<string?> read, string? expected)
    {
        using var timeout = new CancellationTokenSource();
        try
        {
            Assert.That(
                await Task.WhenAny(read, Task.Delay(TimeSpan.FromMinutes(1), timeout.Token)),
                Is.SameAs(read),
                "A complete FIFO line must arrive while the writer stays open and sends nothing else."
            );
        }
        finally
        {
            timeout.Cancel();
        }
        Assert.That(await read, Is.EqualTo(expected));
    }

    sealed class Fifo : IDisposable
    {
        readonly string path = Path.Combine(Path.GetTempPath(), "conduit-fifo-test-" + Guid.NewGuid().ToString("N"));
        readonly FileStream input;

        internal Fifo(bool asynchronous = true)
        {
            if (mkfifo(path, 0x180) != 0)
                throw new IOException($"Could not create test FIFO: {Marshal.GetLastWin32Error()}.");

            var keeper = open(path, 2 | 0x800); // keep both ends open during setup
            if (keeper < 0)
                throw new IOException($"Could not open test FIFO: {Marshal.GetLastWin32Error()}.");
            try
            {
                input = BridgeFifoStreams.OpenRead(path, asynchronous);
                Writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
                Reader = new(input, Encoding.UTF8, false, 1024, true);
            }
            finally
            {
                close(keeper);
            }
        }

        internal FileStream Writer { get; }
        internal StreamReader Reader { get; }

        internal async Task SendAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await Writer.WriteAsync(bytes, 0, bytes.Length);
            await Writer.FlushAsync();
        }

        public void Dispose()
        {
            Writer.Dispose();
            Reader.Dispose();
            input.Dispose();
            File.Delete(path);
        }

        [DllImport("libc", SetLastError = true)]
        static extern int mkfifo(string path, uint mode);

        [DllImport("libc", SetLastError = true)]
        static extern int open(string path, int flags);

        [DllImport("libc")]
        static extern int close(int descriptor);
    }
}
