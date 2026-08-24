#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit.Runtime
{
    static class RuntimeBridgeDispatcher
    {
        static readonly ConcurrentQueue<RuntimeRequest> requests = new();
        static bool executing;
        static int requestCount;

        internal static void Enqueue(
            RuntimeBridgeSession session,
            string requestId,
            BridgeCommand command)
        {
            requests.Enqueue(new(session, requestId, command));
            Interlocked.Increment(ref requestCount);
        }

        internal static void Pump()
        {
            if (Volatile.Read(ref executing)
                || Volatile.Read(ref requestCount) == 0
                || !requests.TryDequeue(out var request))
                return;

            Interlocked.Decrement(ref requestCount);
            Volatile.Write(ref executing, true);
            ExecuteAsync(request);
        }

        static async void ExecuteAsync(RuntimeRequest request)
        {
            var ct = request.Session.Begin(request.RequestId);
            BridgeCommandResult result;
            try
            {
                result = await RuntimeToolDispatcher.ExecuteAsync(request.Command, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = new()
                {
                    outcome = ToolOutcome.Cancelled,
                    diagnostic = "The request was cancelled.",
                };
            }
            catch (Exception exception)
            {
                result = BridgeCommandResult.FromException(
                    BridgeExceptionFormatter.UnwrapTargetInvocationException(exception)
                );
            }

            request.Session.Complete(request.RequestId);
            Volatile.Write(ref executing, false); // a closing one-command FIFO must not hold the player command queue
            try
            {
                await request.Session.SendAsync(
                    BridgeMessage.CreateCommandResult(request.RequestId, result)
                );
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }

        readonly struct RuntimeRequest
        {
            internal RuntimeRequest(
                RuntimeBridgeSession session,
                string requestId,
                BridgeCommand command)
            {
                Session = session;
                RequestId = requestId;
                Command = command;
            }

            internal RuntimeBridgeSession Session { get; }
            internal string RequestId { get; }
            internal BridgeCommand Command { get; }
        }
    }
}
