namespace Conduit;

public sealed partial class UnityBridgeClient
{
    static readonly TimeSpan processExitConfirmationWindow = TimeSpan.FromMilliseconds(250);

    internal async Task<BridgeClientResult> ExecuteIdempotentCommandAsync(
        string projectPath,
        string requestId,
        BridgeCommand command,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await ExecuteCommandAsync(
            projectPath,
            requestId,
            command,
            timeout,
            processIdHint: null,
            ct
        );
        if (result.Handshake is null
            || result.FailureKind is not (BridgeRuntimeFailureKind.SendFailed
                or BridgeRuntimeFailureKind.SendTimedOut
                or BridgeRuntimeFailureKind.StartAckDisconnected
                or BridgeRuntimeFailureKind.StartAckTimedOut
                or BridgeRuntimeFailureKind.ResultDisconnected
                or BridgeRuntimeFailureKind.ResultTimedOut))
            return result;

        return await ExecuteCommandAsync(
            projectPath,
            requestId,
            command,
            timeout,
            processIdHint: null,
            ct
        );
    }

    static async Task<BridgeClientResult> WaitForCommandResultAsync(
        BridgeClientConnection connection,
        BridgeProjectHandshake handshake,
        string requestId,
        string commandType,
        TimeSpan timeout,
        BridgeCommand command,
        CancellationToken commandCancellation,
        CancellationToken ct
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var cancellationMonitorCts = commandCancellation.CanBeCanceled
            ? new CancellationTokenSource()
            : null;
        var effectiveToken = timeoutCts.Token;
        var commandSent = false;
        var cancellationTask = Task.CompletedTask;
        Task<BridgeClientResult?>? processExitTask = null;
        var pending = connection.RegisterRequest(requestId, commandType);

        try
        {
            if (await connection.SendCommandAsync(requestId, command, effectiveToken) is { } sendFailure)
                return sendFailure;

            commandSent = true;
            pending.MarkSent();
            if (cancellationMonitorCts is not null)
                cancellationTask = SendCancellationWhenRequestedAsync(
                    connection,
                    requestId,
                    commandType,
                    commandCancellation,
                    cancellationMonitorCts.Token
                );
            processExitTask = CreateProcessExitTask(
                handshake,
                commandType,
                commandSent,
                effectiveToken
            );

            var startWaitTask = connection.WaitForCommandStartedAsync(pending, effectiveToken, ct);
            if (processExitTask is not null)
            {
                var completedStartTask = await Task.WhenAny(startWaitTask, processExitTask);
                if (ReferenceEquals(completedStartTask, processExitTask) && await processExitTask is { } processFailure)
                    return processFailure;
            }

            var startOutcome = await startWaitTask;
            if (startOutcome.Failure is { } startFailure)
                return await PreferProcessExitAsync(startFailure, processExitTask);

            if (startOutcome.FinalResult is { } earlyResult)
                return earlyResult;

            var waitForResultTask = connection.WaitForResultAsync(pending, timeout, effectiveToken, ct);
            if (processExitTask is not null)
            {
                var completedTask = await Task.WhenAny((Task)waitForResultTask, processExitTask);
                if (ReferenceEquals(completedTask, processExitTask) && await processExitTask is { } processFailure)
                    return processFailure;
            }

            return await PreferProcessExitAsync(await waitForResultTask, processExitTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return commandSent
                ? BridgeClientResult.Failure(
                    handshake,
                    BridgeRuntimeFailureKind.ResultTimedOut,
                    $"Unity did not report completion for '{commandType}' within {timeout}.",
                    commandSent
                )
                : BridgeClientResult.Failure(
                    handshake,
                    BridgeRuntimeFailureKind.SendTimedOut,
                    $"Timed out while trying to send '{commandType}' to Unity.",
                    commandSent
                );
        }
        finally
        {
            connection.RemoveRequest(requestId, pending);
            timeoutCts.Cancel();
            if (cancellationMonitorCts is not null
                && !commandCancellation.IsCancellationRequested)
                cancellationMonitorCts.Cancel();

            if (processExitTask is not null)
                await processExitTask;
            await cancellationTask;
        }

        static async Task SendCancellationWhenRequestedAsync(
            BridgeClientConnection connection,
            string requestId,
            string commandType,
            CancellationToken commandCancellation,
            CancellationToken stopMonitoring
        )
        {
            if (!commandCancellation.CanBeCanceled)
                return;

            if (!commandCancellation.IsCancellationRequested)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(
                    commandCancellation,
                    stopMonitoring
                );

                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token);
                }
                catch (OperationCanceledException) { }
            }

            if (!commandCancellation.IsCancellationRequested)
                return;

            using var sendCts = new CancellationTokenSource(commandCancellationSendTimeout);
            await connection.SendCancelCommandAsync(requestId, commandType, sendCts.Token);
        }
    }

    internal static async Task<BridgeClientResult> PreferProcessExitAsync(
        BridgeClientResult execution,
        Task<BridgeClientResult?>? processExitTask,
        TimeProvider? timeProvider = null
    )
    {
        if (processExitTask is null
            || execution.FailureKind is not (BridgeRuntimeFailureKind.StartAckDisconnected
                or BridgeRuntimeFailureKind.ResultDisconnected))
            return execution;

        // pipe EOF can precede the OS process-exit signal; briefly let the stronger diagnosis win.
        try
        {
            return await processExitTask.WaitAsync(
                processExitConfirmationWindow,
                timeProvider ?? TimeProvider.System
            ) ?? execution;
        }
        catch (TimeoutException)
        {
            return execution;
        }
    }
}
