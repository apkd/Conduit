using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Conduit;

/// <summary>Attempts to recover Unity editors blocked by the changed-open-scene reload prompt.</summary>
public sealed class UnitySceneReloadPromptRecovery(ILogger<UnitySceneReloadPromptRecovery> logger)
{
    internal const int RecoveryTimeoutMilliseconds = 5000;

    /// <summary>Accepts the reload prompt for the specified Unity editor process when a safe match is found.</summary>
    public async Task<bool> TryDismissAsync(string projectPath, int? processId, CancellationToken ct)
    {
        if (processId is not > 0)
            return false;

        try
        {
            bool dismissed = false;
            if (OperatingSystem.IsWindows())
                dismissed = await WindowsSceneReloadPromptRecovery.TryDismissAsync(processId.Value, ct);

            if (OperatingSystem.IsLinux())
                dismissed = LinuxSceneReloadPromptRecovery.TryDismiss(processId.Value);

            if (dismissed)
                // every recovery action is logged because accepting reload can discard clean in-memory scene state.
                logger.ZLogInformation($"Accepted Unity scene reload prompt for project {projectPath} on Unity pid {processId.Value}.");

            return dismissed;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.ZLogDebug($"Scene reload prompt recovery failed for project {projectPath} on Unity pid {processId.Value}.", exception);
        }

        return false;
    }

    internal static bool IsSceneReloadPromptText(string? text) =>
        UnityWindowTitleClassifier.IsSceneReloadPromptText(text);

    internal static Process? TryStartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch
        {
            return null;
        }
    }
}
