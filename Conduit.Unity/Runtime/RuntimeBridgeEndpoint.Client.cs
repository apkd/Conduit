#nullable enable

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Debug = UnityEngine.Debug;

namespace Conduit.Runtime
{
    sealed partial class RuntimeBridgeEndpoint : IDisposable
    {
        async Task RunClientAsync(RuntimeDuplexConnection connection, CancellationToken endpointToken)
        {
            RuntimeBridgeSession? session = null;
            try
            {
                string? payload;
                using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(endpointToken))
                {
                    handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    var readTask = connection.Reader.ReadLineAsync();
                    var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, handshakeCts.Token);
                    if (await Task.WhenAny(readTask, timeoutTask) != readTask)
                        handshakeCts.Token.ThrowIfCancellationRequested();

                    handshakeCts.Cancel();
                    payload = await readTask;
                }

                var request = BridgeProtocol.Deserialize(payload ?? string.Empty);
                if (request?.message_type != BridgeMessageTypes.Hello
                    || request.project == null
                    || request.protocol_version != BridgeProtocol.Version
                    || request.project.process_id is > 0 && request.project.process_id != descriptor.process_id
                    || request.project.session_instance_id is { Length: > 0 }
                    && request.project.session_instance_id != descriptor.session_instance_id)
                    return;

                // an accepted connection is direct proof of liveness even if the periodic lease was delayed.
                handshake.last_seen_utc = WriteDescriptor();
                await connection.WriteAsync(
                    BridgeProtocol.Serialize(BridgeMessage.CreateHello(handshake)),
                    endpointToken
                );

                session = new(
                    Interlocked.Increment(ref nextSessionId),
                    connection,
                    endpointDirectory,
                    endpointToken
                );
                sessions[session.Id] = session;
                while (!endpointToken.IsCancellationRequested && connection.IsConnected)
                {
                    payload = await connection.Reader.ReadLineAsync();
                    if (payload == null)
                        break;

                    var message = BridgeProtocol.Deserialize(payload);
                    if (message?.request_id is not { Length: > 0 })
                        continue;

                    if (message.message_type == BridgeMessageTypes.CancelCommand)
                    {
                        session.Cancel(message.request_id);
                        continue;
                    }

                    if (message.message_type != BridgeMessageTypes.Command || message.command == null)
                        continue;

                    await session.SendAsync(BridgeMessage.CreateCommandStarted(message.request_id));
                    try
                    {
                        session.ResolveArtifacts(message.command.artifacts);
                    }
                    catch (Exception exception)
                    {
                        await session.SendAsync(
                            BridgeMessage.CreateCommandResult(
                                message.request_id,
                                BridgeCommandResult.FromException(exception)
                            )
                        );
                        continue;
                    }

                    RuntimeBridgeDispatcher.Enqueue(session, message.request_id, message.command);
                }
            }
            catch (OperationCanceledException) when (!endpointToken.IsCancellationRequested) { }
            catch (OperationCanceledException) when (endpointToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Debug.LogWarning($"Conduit player client disconnected: {exception.Message}");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                if (session != null)
                    sessions.TryRemove(session.Id, out _);
                connection.Dispose();
            }
        }
    }
}

