#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ConduitConnection
    {
        static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readTask = reader.ReadLineAsync();
            var timeoutTask = Task.Delay(sendTimeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(readTask, timeoutTask);
            if (completedTask != readTask)
                throw new OperationCanceledException(cancellationToken);

            timeoutCts.Cancel();
            return await readTask;
        }

        static async Task<string?> ReadIncomingPayloadAsync(ClientSession session, CancellationToken cancellationToken)
        {
            var readTask = session.Reader.ReadLineAsync();
            if (ShouldKeepConnectionOpen(session.ID))
                return await readTask;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(idleReceiveTimeout, timeoutCts.Token);
            var completedTask = await Task.WhenAny(readTask, timeoutTask);
            if (completedTask == readTask)
            {
                timeoutCts.Cancel();
                return await readTask;
            }

            if (ShouldKeepConnectionOpen(session.ID))
                return await readTask;

            ConduitDiagnostics.Warn($"Closing idle Unity MCP pipe connection after {idleReceiveTimeout.TotalSeconds:0} seconds without incoming messages.");

            try
            {
                session.Reader.Dispose();
            }
            catch (Exception) { }

            session.Connection.Dispose();

            try
            {
                await readTask;
            }
            catch (Exception) { }

            return null;
        }
    }
}

