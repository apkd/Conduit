using System;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    async Task<ToolExecutionResult> CompletePlayerRestartAsync(
        ToolExecutionResult result,
        CancellationToken ct)
    {
        if (result.Outcome != ToolOutcome.Success
            || string.IsNullOrWhiteSpace(result.ReturnValue))
            return result;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(result.ReturnValue);
            var root = document.RootElement;
            var processId = root.GetProperty("process_id").GetInt32();
            var handoffToken = root.GetProperty("handoff_token").GetString();
            if (processId <= 0 || string.IsNullOrWhiteSpace(handoffToken))
                throw new InvalidDataException("The player restart response omitted its handoff identity.");

            var endpoint = await playerDiscovery.WaitForHandoffAsync(
                handoffToken,
                processId,
                TimeSpan.FromSeconds(20),
                ct
            );
            if (endpoint is null)
                return ToolExecutionResult.Timeout(
                    TimeSpan.FromSeconds(20),
                    $"The replacement player process {processId} did not advertise its bridge endpoint."
                );

            return ToolExecutionResult.Success(
                string.Empty,
                $"Player restarted.\nLIVE PLAYER PROCESS ID: `{endpoint.Selector}`"
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ToolExecutionResult.FromException(
                exception,
                result.Logs ?? string.Empty,
                "The player restarted but returned an invalid handoff response."
            );
        }
    }

    async Task ShutdownTestPlayersAsync(string projectPath, ToolExecutionResult testResult)
    {
        var players = playerDiscovery.FindForProject(projectPath)
            .Where(static endpoint => endpoint.IsTestPlayer)
            .ToArray();
        if (players.Length == 0)
            return;

        using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(
            applicationLifetime.ApplicationStopping
        );
        shutdownCts.CancelAfter(TimeSpan.FromSeconds(15));
        var failures = new List<string>();

        // the test framework can drop its queued quit message when it disconnects; use the independent bridge
        foreach (var player in players)
        {
            try
            {
                var execution = await bridgeClient.ExecuteCommandAsync(
                    player.Selector,
                    BridgeIdentifiers.CreateRequestId(),
                    new()
                    {
                        CommandType = BridgeCommandTypes.QuitPlayer,
                        TrackUsage = false,
                    },
                    UnityToolTimeouts.StatusCommand,
                    processIdHint: null,
                    shutdownCts.Token
                );
                if (execution.Result?.Outcome != ToolOutcome.Success
                    && execution.FailureKind != BridgeRuntimeFailureKind.ProcessExited
                    && IsLive(player))
                    failures.Add(
                        $"{player.Selector}: "
                        + (execution.Result?.Diagnostic
                           ?? execution.FailureDiagnostic
                           ?? "the player did not accept its shutdown request")
                    );
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                failures.Add($"{player.Selector}: {exception.Message}");
            }
        }

        try
        {
            while (AnyLive(players))
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, shutdownCts.Token);
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested) { }

        var remainingPlayers = playerDiscovery.Discover();
        foreach (var player in players)
            if (IsInSnapshot(player, remainingPlayers))
                failures.Add($"{player.Selector}: the player did not exit within 15 seconds");

        if (failures.Count > 0)
            testResult.Diagnostic = string.Join(
                "\n",
                new[] { testResult.Diagnostic, "Player shutdown failed: " + string.Join("; ", failures) }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
            );

        bool IsLive(BridgeEndpointDescriptor endpoint)
            => IsInSnapshot(endpoint, playerDiscovery.Discover());

        bool AnyLive(IReadOnlyList<BridgeEndpointDescriptor> expected)
        {
            var live = playerDiscovery.Discover();
            foreach (var endpoint in expected)
                if (IsInSnapshot(endpoint, live))
                    return true;

            return false;
        }

        static bool IsInSnapshot(
            BridgeEndpointDescriptor endpoint,
            IReadOnlyList<BridgeEndpointDescriptor> live)
        {
            foreach (var candidate in live)
                if (string.Equals(
                        candidate.SessionInstanceId,
                        endpoint.SessionInstanceId,
                        StringComparison.Ordinal
                    ))
                    return true;

            return false;
        }
    }
}
