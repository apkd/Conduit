using System.Text;

namespace Conduit;

public sealed class BridgePayloadBufferTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(16 * 1024)]
    [Arguments(16 * 1024 + 1)]
    [Arguments(64 * 1024)]
    [Arguments(2 * 1024 * 1024)]
    public async Task PayloadBytesAndSeparateTerminatorAreUnchanged(int length, CancellationToken ct)
    {
        using var output = new RecordingStream();
        await using var transport = CreateTransport(output);
        var payload = new string('x', length);

        await transport.WritePayloadAsync(payload, ct);

        await Assert.That(output.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(payload + "\n"))).IsTrue();
        await Assert.That(output.WriteSizes[^1]).IsEqualTo(1);
        await Assert.That(output.FlushCount).IsEqualTo(1);
    }

    [Test]
    public async Task LargePayloadWritesStayBoundedAsMessagesGrow(CancellationToken ct)
    {
        var payload = new string('x', 2 * 1024 * 1024);
        using var first = new RecordingStream();
        using var second = new RecordingStream();
        await using var firstTransport = CreateTransport(first);
        await using var secondTransport = CreateTransport(second);

        await firstTransport.WritePayloadAsync(payload, ct);
        await secondTransport.WritePayloadAsync(payload + payload, ct);

        await Assert.That(first.WriteSizes.Max()).IsLessThan(payload.Length);
        await Assert.That(second.WriteSizes.Max()).IsEqualTo(first.WriteSizes.Max());
        await Assert.That(second.WriteSizes.Count).IsGreaterThan(first.WriteSizes.Count);
    }

    [Test]
    public async Task ChunkBoundaryPreservesSurrogatesAndReplacementFallback(CancellationToken ct)
    {
        using var output = new RecordingStream();
        await using var transport = CreateTransport(output);
        var payload = new string('x', 65_535) + "\U0001F642\ud800z\udc00"
                      + string.Concat(Enumerable.Repeat("雪\U0001F642", 20_000));

        await transport.WritePayloadAsync(payload, ct);

        var expected = new UTF8Encoding(false).GetBytes(payload + "\n");
        await Assert.That(output.ToArray().SequenceEqual(expected)).IsTrue();
        await Assert.That(output.WriteSizes[^1]).IsEqualTo(1);
    }

    [Test]
    public async Task PreCancelledWriteDoesNotTouchStream(CancellationToken ct)
    {
        using var output = new RecordingStream();
        await using var transport = CreateTransport(output);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.Cancel();

        await Assert.That(() => transport.WritePayloadAsync("test", cancellation.Token)).Throws<OperationCanceledException>();

        await Assert.That(output.Length).IsEqualTo(0L);
        await Assert.That(output.WriteSizes.Count).IsEqualTo(0);
        await Assert.That(output.FlushCount).IsEqualTo(0);
    }

    [Test]
    public async Task CancellationBetweenChunksDoesNotAppendTerminator(CancellationToken ct)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var output = new RecordingStream { AfterWrite = cancellation.Cancel };
        await using var transport = CreateTransport(output);

        var payload = new string('x', 2 * 1024 * 1024);
        await Assert.That(() => transport.WritePayloadAsync(payload, cancellation.Token)).Throws<OperationCanceledException>();

        await Assert.That(output.Length).IsGreaterThan(0L).And.IsLessThan(payload.Length);
        await Assert.That(output.ToArray().Contains((byte)'\n')).IsFalse();
        await Assert.That(output.WriteSizes.Count).IsEqualTo(1);
        await Assert.That(output.FlushCount).IsEqualTo(0);
    }

    [Test]
    public async Task WriteFailureDoesNotAppendTerminator(CancellationToken ct)
    {
        using var output = new RecordingStream { FailOnWrite = 2 };
        await using var transport = CreateTransport(output);

        var payload = new string('x', 2 * 1024 * 1024);
        await Assert.That(() => transport.WritePayloadAsync(payload, ct)).Throws<IOException>();

        await Assert.That(output.Length).IsGreaterThan(0L).And.IsLessThan(payload.Length);
        await Assert.That(output.ToArray().Contains((byte)'\n')).IsFalse();
        await Assert.That(output.WriteSizes.Count).IsEqualTo(1);
        await Assert.That(output.FlushCount).IsEqualTo(0);
    }

    static BridgeTransport CreateTransport(Stream output)
        => BridgeTransport.FromStreams(Stream.Null, output, () => true, () => ValueTask.CompletedTask);

    sealed class RecordingStream : Stream
    {
        readonly MemoryStream content = new();
        int attemptedWrites;
        internal List<int> WriteSizes { get; } = [];
        internal int FlushCount { get; private set; }
        internal int FailOnWrite { get; init; }
        internal Action? AfterWrite { get; init; }
        internal byte[] ToArray() => content.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => content.Length;
        public override long Position { get => content.Position; set => throw new NotSupportedException(); }
        public override void Flush() => FlushCount++;
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++attemptedWrites == FailOnWrite)
                throw new IOException("Injected write failure.");
            content.Write(buffer.Span);
            WriteSizes.Add(buffer.Length);
            AfterWrite?.Invoke();
            return ValueTask.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                content.Dispose();
            base.Dispose(disposing);
        }
    }
}
