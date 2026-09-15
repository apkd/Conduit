#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Conduit;
using Conduit.Runtime;
using NUnit.Framework;

public sealed class RuntimeTransportTests
{
    [Test]
    public async Task DisposeStopsSynchronousReaderBeforeReturning()
    {
        using var input = new BlockingReadStream();
        using var output = new MemoryStream();
        using var connection = new RuntimeDuplexConnection(input, output, static () => true, readSynchronously: true);
        var read = connection.Reader.ReadLineAsync();
        await CompletesAsync(input.Entered.Task);

        connection.Dispose();

        Assert.That(input.Exited, Is.True);
        Assert.That(connection.IsConnected, Is.False);
        Assert.That(output.CanWrite, Is.False);
        await CompletesAsync(read);
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await read);
        connection.Dispose();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EndpointShutdownClosesClientsDuringHandshakeAndAfterHello(bool handshakeComplete)
    {
        using var endpoint = new RuntimeBridgeEndpoint();
        using var shutdown = new CancellationTokenSource();
        var hello = BridgeMessage.CreateHello(new BridgeProjectHandshake
        {
            session_instance_id = endpoint.SessionInstanceId,
        });
        using var input = new BlockingReadStream(handshakeComplete ? BridgeProtocol.Serialize(hello) + "\n" : "");
        using var output = new SignaledWriteStream();
        using var connection = new RuntimeDuplexConnection(input, output, static () => true, readSynchronously: true);
        var client = (Task)typeof(RuntimeBridgeEndpoint)
            .GetMethod("RunClientAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(endpoint, new object[] { connection, shutdown.Token });
        await CompletesAsync(input.Entered.Task);
        if (handshakeComplete)
            await CompletesAsync(output.Written.Task);

        shutdown.Cancel();

        Assert.That(input.Exited, Is.True);
        Assert.That(connection.IsConnected, Is.False);
        await CompletesAsync(client);
        await client;
    }

    [Test]
    public async Task ClosingConnectionPreservesPendingWriteFailure()
    {
        using var input = new MemoryStream();
        using var output = new BlockingWriteStream();
        using var connection = new RuntimeDuplexConnection(input, output, static () => true);
        var write = connection.WriteAsync("pending", CancellationToken.None);

        connection.Dispose();

        await CompletesAsync(write);
        Assert.ThrowsAsync<IOException>(async () => await write);
    }

    static async Task CompletesAsync(Task task)
    {
        using var timeout = new CancellationTokenSource();
        try
        {
            Assert.That(
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token)),
                Is.SameAs(task),
                "The transport must finish without another message or the peer closing."
            );
        }
        finally
        {
            timeout.Cancel();
        }
    }

    sealed class SignaledWriteStream : MemoryStream
    {
        internal readonly TaskCompletionSource<bool> Written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await base.WriteAsync(buffer, offset, count, cancellationToken);
            Written.TrySetResult(true);
        }
    }

    sealed class BlockingWriteStream : MemoryStream
    {
        readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => completion.Task;

        protected override void Dispose(bool disposing)
        {
            completion.TrySetException(new IOException("The peer closed during the write."));
            base.Dispose(disposing);
        }
    }

    sealed class BlockingReadStream : MemoryStream
    {
        readonly ManualResetEventSlim stopped = new(false);
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Exited { get; private set; }

        internal BlockingReadStream(string prefix = "") : base(Encoding.UTF8.GetBytes(prefix)) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            if (read != 0)
                return read;
            Entered.TrySetResult(true);
            stopped.Wait();
            Exited = true;
            throw new ObjectDisposedException(nameof(BlockingReadStream));
        }

        protected override void Dispose(bool disposing)
        {
            stopped.Set();
            base.Dispose(disposing);
        }
    }
}
