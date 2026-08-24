#nullable enable

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ConduitConnection
    {
        static void WritePayload(Stream stream, string payload)
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
                stream.Write(buffer, 0, written);
            }
            finally
            {
                if (buffer.Length <= MaximumPooledPayloadBufferLength)
                    ArrayPool<byte>.Shared.Return(buffer);
            }

            stream.Flush();
        }

        static async Task WritePayloadAsync(Stream stream, string payload, CancellationToken cancellationToken)
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
                await stream.WriteAsync(buffer, 0, written, cancellationToken);
            }
            finally
            {
                if (buffer.Length <= MaximumPooledPayloadBufferLength)
                    ArrayPool<byte>.Shared.Return(buffer);
            }

            await stream.FlushAsync(cancellationToken);
        }

        static bool TryQueueMessage(ClientSession session, OutboundClientMessage outboundMessage)
        {
            if (session.IsDisposed || session.WriterCancellation.IsCancellationRequested)
                return false;

            session.OutboundMessages.Enqueue(outboundMessage);
            try
            {
                session.OutboundSignal.Release();
                return true;
            }
            catch (ObjectDisposedException)
            {
                outboundMessage.TrySetResult(false);
                return false;
            }
        }

        static async Task RunWriteLoopAsync(ClientSession session)
        {
            try
            {
                while (!session.WriterCancellation.IsCancellationRequested)
                {
                    await session.OutboundSignal.WaitAsync(session.WriterCancellation.Token);
                    while (session.OutboundMessages.TryDequeue(out var outboundMessage))
                    {
                        try
                        {
                            await WritePayloadAsync(
                                session.Connection.Output,
                                outboundMessage.Payload,
                                session.WriterCancellation.Token
                            );
                            outboundMessage.TrySetResult(true);
                        }
                        catch (OperationCanceledException) when (session.WriterCancellation.IsCancellationRequested) { }
                        catch (Exception exception)
                        {
                            if (!IsShuttingDown())
                                ConduitDiagnostics.Error($"Failed to send {outboundMessage.Context}.", exception);
                        }

                        if (outboundMessage.Completion.Task.IsCompleted)
                            continue;

                        outboundMessage.TrySetResult(false);
                        ClearConnection(session);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (session.WriterCancellation.IsCancellationRequested) { }
            finally
            {
                FailPendingWrites(session);
            }
        }

        static void FailPendingWrites(ClientSession session)
        {
            while (session.OutboundMessages.TryDequeue(out var outboundMessage))
                outboundMessage.TrySetResult(false);
        }
    }
}

