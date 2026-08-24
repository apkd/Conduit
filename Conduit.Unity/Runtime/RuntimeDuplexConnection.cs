#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Runtime
{
    sealed class RuntimeDuplexConnection : IDisposable
    {
        const int MaximumPooledPayloadBufferLength = 1024 * 1024;
        static readonly UTF8Encoding utf8NoBom = new(false);
        readonly Stream input;
        readonly Stream output;
        readonly Func<bool> isConnected;
        readonly SemaphoreSlim writeGate = new(1, 1);
        bool disposed;

        internal RuntimeDuplexConnection(Stream input, Stream output, Func<bool> isConnected)
        {
            this.input = input;
            this.output = output;
            this.isConnected = isConnected;
            Reader = new(input, utf8NoBom, false, 1024, true);
        }

        internal StreamReader Reader { get; }

        internal bool IsConnected => !disposed && isConnected();

        internal static RuntimeDuplexConnection FromSingleStream(Stream stream, Func<bool> connected)
            => new(stream, stream, connected);

        internal async Task WriteAsync(string payload, CancellationToken ct)
        {
            await writeGate.WaitAsync(ct);
            try
            {
                var byteCount = utf8NoBom.GetByteCount(payload);
                var bufferLength = checked(byteCount + 1);
                var buffer = bufferLength <= MaximumPooledPayloadBufferLength
                    ? ArrayPool<byte>.Shared.Rent(bufferLength)
                    : new byte[bufferLength];
                try
                {
                    var written = utf8NoBom.GetBytes(payload.AsSpan(), buffer.AsSpan());
                    buffer[written++] = (byte)'\n';
                    await output.WriteAsync(buffer, 0, written, ct);
                    await output.FlushAsync(ct);
                }
                finally
                {
                    if (buffer.Length <= MaximumPooledPayloadBufferLength)
                        ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            finally
            {
                writeGate.Release();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            Reader.Dispose();
            if (!ReferenceEquals(input, output))
                input.Dispose();
            output.Dispose();
            writeGate.Dispose();
        }
    }
}
