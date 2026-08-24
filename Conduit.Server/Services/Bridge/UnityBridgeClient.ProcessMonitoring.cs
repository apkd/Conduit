using System.Diagnostics;

namespace Conduit;

public sealed partial class UnityBridgeClient
{
    static Task<BridgeClientResult?>? CreateProcessExitTask(
        BridgeProjectHandshake handshake,
        string commandType,
        bool commandSent,
        CancellationToken ct
    )
    {
        if (!handshake.CanMonitorProcess || handshake.EffectiveProcessId is not > 0)
            return null;

        var process = ProcessInspection.TryGetProcess(handshake.EffectiveProcessId);
        return process is null
            ? null
            : WaitForProcessExitAsync(handshake, process, commandType, commandSent, ct);
    }

    static async Task<BridgeClientResult?> WaitForProcessExitAsync(
        BridgeProjectHandshake handshake,
        Process process,
        string context,
        bool commandSent,
        CancellationToken ct
    )
    {
        try
        {
            await process.WaitForExitAsync(ct);
            return ProcessExited(handshake, context, commandSent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    static BridgeClientResult ProcessExited(BridgeProjectHandshake handshake, string context, bool commandSent) =>
        BridgeClientResult.Failure(
            handshake,
            BridgeRuntimeFailureKind.ProcessExited,
            $"Unity {handshake.EndpointKind} process {handshake.EffectiveProcessId} exited while '{context}' was running.",
            commandSent
        );

    static async Task DisposeConnectionAsync(BridgeTransport? transport)
    {
        if (transport is not null)
            await transport.DisposeAsync();
    }
}
