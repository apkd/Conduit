using System.Net.Sockets;
using ZLogger;

namespace Conduit;

public sealed partial class UnityBridgeClient
{
    async Task<(BridgeClientConnection? Connection, BridgeClientResult Result)> TryConnectUntilReadyAsync(
        string projectPath,
        DateTimeOffset deadline,
        CancellationToken timeoutToken,
        CancellationToken callerToken
    )
    {
        BridgeClientResult? lastFailure = null;
        var playerSelector = PlayerSelector.TryParse(projectPath, out var parsedSelector)
            ? parsedSelector
            : (PlayerSelector?)null;
        var pipeName = playerSelector is null ? BridgeIdentifiers.GetPipeName(projectPath) : null;

        try
        {
            while (!timeoutToken.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
            {
                var connectResult = await TryConnectAsync(
                    projectPath,
                    playerSelector,
                    pipeName,
                    timeoutToken
                );
                if (connectResult.Connection is not null)
                    return connectResult;

                lastFailure = connectResult.Result;
                if (lastFailure.FailureKind is BridgeRuntimeFailureKind.AmbiguousTarget
                    or BridgeRuntimeFailureKind.ProtocolMismatch)
                    break;
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                var delay = remaining < connectRetryDelay ? remaining : connectRetryDelay;
                await Task.Delay(delay, timeoutToken);
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { }

        return (null, lastFailure ?? BridgeClientResult.Failure(
            handshake: null,
            BridgeRuntimeFailureKind.ConnectTimedOut,
            $"Could not establish a Unity connection for '{projectPath}' in time.",
            commandSent: false
        ));
    }

    async Task<(BridgeClientConnection? Connection, BridgeClientResult Result)> TryConnectAsync(
        string normalizedProjectPath,
        PlayerSelector? playerSelector,
        string? pipeName,
        CancellationToken ct)
    {
        BridgeTransport? transport = null;

        try
        {
            BridgeEndpointDescriptor? endpoint = null;
            if (playerSelector is { } selector)
            {
                var resolution = await playerDiscovery.ResolveAsync(selector, ct);
                if (resolution.Endpoint is null)
                    return (null, BridgeClientResult.Failure(
                        handshake: null,
                        resolution.IsAmbiguous
                            ? BridgeRuntimeFailureKind.AmbiguousTarget
                            : BridgeRuntimeFailureKind.ConnectTimedOut,
                        resolution.Diagnostic!,
                        commandSent: false
                    ));

                endpoint = resolution.Endpoint;
                transport = await BridgeTransport.ConnectAsync(endpoint, connectAttemptTimeout, ct);
            }
            else
                transport = await BridgeTransport.ConnectAsync(pipeName!, connectAttemptTimeout, ct);

            try
            {
                var hello = BridgeMessage.CreateHello(
                    endpoint is null
                        ? new() { ProjectPath = normalizedProjectPath }
                        : new()
                        {
                            EndpointKind = BridgeEndpointKinds.Player,
                            ProcessId = endpoint.ProcessId,
                            SessionInstanceId = endpoint.SessionInstanceId,
                        }
                );

                await transport.WritePayloadAsync(BridgeProtocol.Serialize(hello), ct);
            }
            catch (IOException exception)
            {
                logger.ZLogDebug($"Unity connection disconnected while sending the hello handshake for '{normalizedProjectPath}'.", exception);
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }
            catch (ObjectDisposedException exception)
            {
                logger.ZLogDebug($"Unity connection disposed the pipe while sending the hello handshake for '{normalizedProjectPath}'.", exception);
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }

            var payload = await transport.ReadLineAsync(ct);
            if (payload is null)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.HandshakeDisconnected,
                    $"The Unity connection for '{normalizedProjectPath}' closed during the hello handshake.",
                    commandSent: false
                ));
            }

            var response = BridgeProtocol.Deserialize(payload);
            if (response?.MessageType != BridgeMessageTypes.Hello)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.InvalidHandshake,
                    $"Unity returned an invalid hello handshake for '{normalizedProjectPath}'. This usually means the editor is reloading.",
                    commandSent: false
                ));
            }

            if (response.ProtocolVersion != BridgeProtocol.Version)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProtocolMismatch,
                    BridgeContract.FormatProtocolMismatch(
                        BridgeProtocol.Version,
                        response.ProtocolVersion
                    ),
                    commandSent: false
                ));
            }

            if (response.Project is null)
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.InvalidHandshake,
                    $"Unity returned an invalid hello handshake for '{normalizedProjectPath}'. This usually means the editor is reloading.",
                    commandSent: false
                ));
            }

            response.Project.ProjectPath = ProjectPathNormalizer.Normalize(response.Project.ProjectPath);
            if (endpoint is null
                && !string.Equals(response.Project.ProjectPath, normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProjectMismatch,
                    $"Unity connection responded for '{response.Project.ProjectPath}' while '{normalizedProjectPath}' was requested.",
                    commandSent: false
                ));
            }

            if (endpoint is not null
                && (response.Project.EndpointKind != BridgeEndpointKinds.Player
                    || response.Project.EffectiveProcessId != endpoint.ProcessId
                    || !string.Equals(
                        response.Project.SessionInstanceId,
                        endpoint.SessionInstanceId,
                        StringComparison.Ordinal
                    )))
            {
                await DisposeConnectionAsync(transport);
                return (null, BridgeClientResult.Failure(
                    handshake: null,
                    BridgeRuntimeFailureKind.ProjectMismatch,
                    $"The endpoint for '{normalizedProjectPath}' changed during its bridge handshake.",
                    commandSent: false
                ));
            }

            return (
                new(
                    transport,
                    response.Project,
                    logger,
                    endpoint?.EndpointDirectoryPath
                ),
                BridgeClientResult.Connected(response.Project)
            );
        }
        catch (TimeoutException)
        {
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch (IOException exception)
        {
            logger.ZLogDebug($"Unity connection attempt failed for '{normalizedProjectPath}'.", exception);
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch (ObjectDisposedException exception)
        {
            logger.ZLogDebug($"Unity connection was disposed while connecting to '{normalizedProjectPath}'.", exception);
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch (SocketException exception)
        {
            logger.ZLogDebug($"Unity socket connection attempt failed for '{normalizedProjectPath}'.", exception);
            await DisposeConnectionAsync(transport);
            return (null, BridgeClientResult.Failure(
                handshake: null,
                BridgeRuntimeFailureKind.ConnectTimedOut,
                $"Could not establish a Unity connection for '{normalizedProjectPath}' in time.",
                commandSent: false
            ));
        }
        catch
        {
            await DisposeConnectionAsync(transport);
            throw;
        }
    }
}
