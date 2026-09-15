#nullable enable

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Runtime;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;

[Platform("Win")]
public sealed class RuntimeWineFifoStreamTests
{
    [TestCase(0)]
    [TestCase(1500)]
    public async Task DisposeCancelsReadWhilePeerRemainsOpen(int prefixLength)
    {
        using var pipe = new Pipe();
        var input = pipe.Input;
        var server = pipe.Output;
        using var reader = new StreamReader(input, Encoding.UTF8, false, 1024, true);
        using var reading = new ManualResetEventSlim();
        var read = Task.Run(() =>
        {
            reading.Set();
            return reader.ReadLine();
        });
        try
        {
            reading.Wait();
            if (prefixLength != 0)
            {
                var prefix = Encoding.UTF8.GetBytes(new string('x', prefixLength));
                await server.WriteAsync(prefix, 0, prefix.Length);
                await server.FlushAsync();
            }
            input.Dispose();

            await CompletesAsync(read);
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await read);
            Assert.That(server.CanWrite, Is.True);
        }
        finally
        {
            input.Dispose();
            try { await read; }
            catch (ObjectDisposedException) { }
        }
    }

    [Test]
    public async Task DelayedFragmentedLinesAndPeerCloseCompleteReads()
    {
        using var pipe = new Pipe();
        var input = pipe.Input;
        var server = pipe.Output;
        using var reader = new StreamReader(input, Encoding.UTF8, false, 1024, true);
        var expected = new string('x', 1023) + "🌍 café";
        var bytes = Encoding.UTF8.GetBytes(expected + "\r\nnext\n");
        var read = Task.Run(() => reader.ReadLine());
        try
        {
            await server.WriteAsync(bytes, 0, 1025);
            await server.FlushAsync();
            Assert.That(read.IsCompleted, Is.False);
            await server.WriteAsync(bytes, 1025, bytes.Length - 1025);
            await server.FlushAsync();
            await CompletesAsync(read);
            Assert.That(await read, Is.EqualTo(expected));
            Assert.That(reader.ReadLine(), Is.EqualTo("next"));

            read = Task.Run(() => reader.ReadLine());
            pipe.CloseOutput();
            await CompletesAsync(read);
            Assert.That(await read, Is.Null);
        }
        finally
        {
            input.Dispose();
            try { await read; }
            catch (ObjectDisposedException) { }
        }
    }

    sealed class Pipe : IDisposable
    {
        readonly SafeFileHandle server;
        internal readonly RuntimeWineFifoStream Input;
        internal readonly FileStream Output;

        internal Pipe()
        {
            var name = @"\\.\pipe\conduit-read-" + Guid.NewGuid().ToString("N");
            server = CreateNamedPipeW(name, 2, 0, 1, 4096, 4096, 0, IntPtr.Zero);
            if (server.IsInvalid)
                throw new Win32Exception();
            try
            {
                Input = new RuntimeWineFifoStream(name);
                // opening the client before accepting is a supported named-pipe connection order
                if (!ConnectNamedPipe(server, IntPtr.Zero) && Marshal.GetLastWin32Error() != 535)
                    throw new Win32Exception();
                Output = new FileStream(server, FileAccess.Write, 1, false);
            }
            catch
            {
                Input?.Dispose();
                server.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Input.Dispose();
            CloseOutput();
        }

        internal void CloseOutput()
        {
            Output.Dispose();
            server.Dispose();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateNamedPipeW(
            string name, uint openMode, uint pipeMode, uint instances, uint outputBuffer, uint inputBuffer, uint timeout, IntPtr security);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ConnectNamedPipe(SafeFileHandle pipe, IntPtr overlapped);
    }

    static async Task CompletesAsync(Task task)
    {
        using var timeout = new CancellationTokenSource();
        try
        {
            Assert.That(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token)), Is.SameAs(task));
        }
        finally
        {
            timeout.Cancel();
        }
    }
}
