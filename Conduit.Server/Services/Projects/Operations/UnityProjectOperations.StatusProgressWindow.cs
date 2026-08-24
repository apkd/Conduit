using System;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    async Task<BridgeClientResult?> TryWaitForStatusProgressWindowAsync(
        string normalizedProjectPath,
        UnityProjectEnvironmentSnapshot snapshot,
        BridgeClientResult? latestExecution,
        TimeSpan statusTimeout,
        StatusUsageState usage,
        CT ct
    )
    {
        // unity blocks editor updates during native progress windows, so the bridge can look dead while the editor is still making progress.
        if (!UnityStatusPolicy.ShouldWaitForStatusProgressWindow(snapshot, latestExecution)
            || snapshot.MatchedProcess is not { } matchedProcess)
            return null;

        var processId = matchedProcess.ProcessId;
        if (TryReadProgressWindowTitle(processId) is not { } progressTitle)
            return null;

        logger.LogInformation(
            "Status detected Unity progress window '{Title}' for project '{ProjectPath}'. Waiting for the editor to respond.",
            progressTitle,
            normalizedProjectPath
        );

        var currentTitle = progressTitle;
        var windowDeadlineUtc = timeProvider.GetUtcNow() + UnityToolTimeouts.StatusProgressInitialWindow;
        var titleChangedInWindow = false;
        var completedTitleChangeExtensions = 0;
        var lastExecution = latestExecution;

        while (true)
        {
            lastExecution = await ExecuteRecoverableStatusCommandAsync(
                normalizedProjectPath,
                processId,
                statusTimeout,
                usage,
                ct
            );
            if (TryParsePingSnapshot(lastExecution, out _))
                return lastExecution;

            if (!UnityStatusPolicy.ShouldWaitForStatusProgressWindow(snapshot, lastExecution))
                return lastExecution;

            if (TryReadProgressWindowTitle(processId) is not { } nextTitle)
                return lastExecution;

            if (!string.Equals(currentTitle, nextTitle, StringComparison.Ordinal))
            {
                logger.LogInformation(
                    "Unity progress window title changed from '{PreviousTitle}' to '{CurrentTitle}' for project '{ProjectPath}'. Extending status wait.",
                    currentTitle,
                    nextTitle,
                    normalizedProjectPath
                );
                currentTitle = nextTitle;
                titleChangedInWindow = true;
            }

            var nowUtc = timeProvider.GetUtcNow();
            if (nowUtc >= windowDeadlineUtc)
            {
                if (!titleChangedInWindow)
                    return lastExecution;

                var extension = UnityStatusPolicy.GetStatusProgressTitleChangeWindow(
                    completedTitleChangeExtensions++
                );
                windowDeadlineUtc = nowUtc + extension;
                titleChangedInWindow = false;
                continue;
            }

            var delay = windowDeadlineUtc - nowUtc;
            if (delay > UnityToolTimeouts.StatusProgressPollInterval)
                delay = UnityToolTimeouts.StatusProgressPollInterval;
            if (delay <= TimeSpan.Zero)
                continue;

            await Task.Delay(delay, timeProvider, ct);
        }
    }

    static string? TryReadProgressWindowTitle(int processId) =>
        UnityWindowTitleProbe
            .TryFindMatchingProcessWindowTitle(processId, UnityWindowTitleClassifier.IsProgressTitle)
            ?.Title;
}
