using System;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    ProjectCommandQueue GetOrCreatePlayerQueue(string playerTarget)
    {
        if (queues.TryGetValue(playerTarget, out var queue))
            return queue;

        lock (queueCreationGate)
        {
            if (queues.TryGetValue(playerTarget, out queue))
                return queue;

            queue = new(
                loggerFactory.CreateLogger<ProjectCommandQueue>(),
                ExecuteQueuedPlayerCommandAsync,
                applicationLifetime.ApplicationStopping
            );
            queues[playerTarget] = queue;
            return queue;
        }
    }

    async Task<ToolExecutionResult> EnqueuePlayerAsync(
        string playerTarget,
        BridgeCommand command,
        CT ct)
    {
        var selector = PlayerSelector.TryParse(playerTarget, out var parsedSelector)
            ? parsedSelector
            : throw new InvalidOperationException($"Player target '{playerTarget}' is invalid.");
        command.IncludeBackgroundLogs = true;
        var resolution = await playerDiscovery.ResolveAsync(selector, ct);
        if (resolution.Endpoint is null)
            return new()
            {
                Outcome = ToolOutcome.NotConnected,
                Diagnostic = resolution.Diagnostic,
            };

        var session = playerSessions.GetOrAdd(playerTarget, static target => new(target));
        var queue = GetOrCreatePlayerQueue(playerTarget);
        var commandTimeout = UnityToolTimeouts.ForCommand(
            BridgeCommandKinds.Parse(command.CommandType)
        );
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(commandTimeout);
        var result = await queue.EnqueueAsync(
            new(session, command, timeoutCts.Token),
            timeoutCts.Token
        );
        return !ct.IsCancellationRequested
               && timeoutCts.IsCancellationRequested
               && result.Outcome == ToolOutcome.Cancelled
            ? ToolExecutionResult.Timeout(
                commandTimeout,
                $"Unity did not start or finish '{command.CommandType}' within {commandTimeout}."
            )
            : result;
    }

    async Task<ToolExecutionResult> ExecuteQueuedPlayerCommandAsync(
        QueuedProjectCommand queuedCommand,
        CT ct)
    {
        var context = queuedCommand.Session.StartCommand(queuedCommand.Command);
        var timeout = UnityToolTimeouts.ForCommand(
            BridgeCommandKinds.Parse(queuedCommand.Command.CommandType)
        );
        var reachable = false;
        try
        {
            var execution = await bridgeClient.ExecuteCommandAsync(
                queuedCommand.Session.ProjectPath,
                context.RequestId,
                queuedCommand.Command,
                timeout,
                processIdHint: null,
                ct,
                queuedCommand.RequestCancellation
            );
            reachable = execution.Handshake is { IsPlayer: true }
                        && execution.FailureKind != BridgeRuntimeFailureKind.ProcessExited;
            var result = execution.Result
                         ?? ToToolExecutionResult(
                             queuedCommand.Session.ProjectPath,
                             queuedCommand.Command.CommandType,
                             execution,
                             timeout
                         );
            result = MaterializePlayerArtifacts(
                queuedCommand.Command.CommandType,
                result,
                execution.Artifacts
            );
            return queuedCommand.Command.CommandType == BridgeCommandTypes.Restart
                ? await CompletePlayerRestartAsync(result, ct)
                : result;
        }
        finally
        {
            queuedCommand.Session.FinishCommand(context.RequestId, reachable);
        }
    }

    static ToolExecutionResult MaterializePlayerArtifacts(
        string commandType,
        ToolExecutionResult result,
        BridgeArtifact[] artifacts)
    {
        if (artifacts.Length == 0 || commandType != BridgeCommandTypes.Screenshot)
            return result;

        try
        {
            var artifact = artifacts[0];
            var directory = Path.Combine(
                Path.GetTempPath(),
                "conduit",
                "player-artifacts"
            );
            Directory.CreateDirectory(directory);
            var extension = Path.GetExtension(artifact.Name);
            var fileName = artifact.Sha256
                           + (string.IsNullOrWhiteSpace(extension) ? ".bin" : extension);
            var path = Path.Combine(directory, fileName);
            File.WriteAllBytes(path, artifact.ReadVerified());

            return new()
            {
                Outcome = result.Outcome,
                DisplayName = result.DisplayName,
                Logs = result.Logs,
                BackgroundLogs = result.BackgroundLogs,
                ReturnValue = $"Player image captured: {path}",
                Exception = result.Exception,
                Diagnostic = result.Diagnostic,
            };
        }
        catch (Exception exception)
        {
            return ToolExecutionResult.FromException(
                exception,
                result.Logs ?? string.Empty,
                "The player screenshot was received but could not be stored by the MCP server.",
                result.BackgroundLogs
            );
        }
    }
}
