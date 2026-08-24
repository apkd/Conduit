using System.Diagnostics;
using System.Text;

namespace Conduit;

public sealed partial class UnityEditorProcessController
{
    async Task<ToolExecutionResult?> TryCreateDirtySceneBlockAsync(string projectPath, int? processIdHint, CancellationToken ct)
    {
        var pingExecution = await bridgeClient.ExecuteCommandAsync(
            projectPath,
            BridgeIdentifiers.CreateRequestId(),
            new() { CommandType = BridgeCommandTypes.Status },
            UnityToolTimeouts.StatusCommand,
            processIdHint,
            ct
        );

        if (pingExecution.Result?.Outcome != ToolOutcome.Success
            || string.IsNullOrWhiteSpace(pingExecution.Result.ReturnValue)
            || !UnityPingSnapshotParser.TryParse(pingExecution.Result.ReturnValue, out var pingSnapshot)
            || pingSnapshot.DirtyScenes.Length == 0)
            return null;

        var builder = new StringBuilder();
        builder.AppendLine("Cannot run 'restart' while scenes have unsaved changes.");
        builder.AppendLine("Dirty scenes:");
        foreach (var dirtyScene in pingSnapshot.DirtyScenes)
            builder.AppendLine("- " + dirtyScene);

        builder.Append("Use '");
        builder.Append(BridgeCommandTypes.SaveScenes);
        builder.Append("' to save them or '");
        builder.Append(BridgeCommandTypes.DiscardScenes);
        builder.Append("' to discard them.");
        return ToolExecutionResult.DirtyScene(builder.ToTrimmedString());
    }

    static async Task<ToolExecutionResult?> TryTerminateExistingEditorAsync(
        Process editorProcess,
        StringBuilder builder,
        CancellationToken ct
    )
    {
        if (await TryCloseGracefullyAsync(editorProcess, ct))
        {
            builder.AppendLine("Graceful shutdown succeeded.");
            return null;
        }

        builder.AppendLine(
            $"Graceful shutdown did not complete within {UnityToolTimeouts.RestartShutdownGracePeriod.TotalSeconds:0} seconds; force killing the editor process tree."
        );
        if (await TryForceKillAsync(editorProcess, ct))
        {
            builder.AppendLine("Force kill succeeded.");
            return null;
        }

        builder.AppendLine(
            $"Force kill did not terminate the editor process tree within {UnityToolTimeouts.RestartShutdownKillWait.TotalSeconds:0} seconds."
        );
        return ToolExecutionResult.Timeout(UnityToolTimeouts.RestartShutdownKillWait, builder.ToTrimmedString());
    }

    static async Task<bool> TryCloseGracefullyAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited)
            return true;

        if (!process.CloseMainWindow())
            return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UnityToolTimeouts.RestartShutdownGracePeriod);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return process.HasExited;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    static async Task<bool> TryForceKillAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited)
            return true;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return process.HasExited;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UnityToolTimeouts.RestartShutdownKillWait);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }

        return process.HasExited;
    }

    internal static bool HasExited(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }
}
