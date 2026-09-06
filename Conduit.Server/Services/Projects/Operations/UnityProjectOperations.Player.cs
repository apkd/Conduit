using System;
using Cysharp.Text;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
    async Task<string> StatusPlayerAsync(PlayerSelector selector, CT ct)
    {
        var target = selector.ToString();
        var resolution = await playerDiscovery.ResolveAsync(selector, ct);
        if (resolution.Endpoint is null)
            return ToolResponseFormatter.Format(
                new()
                {
                    Outcome = ToolOutcome.NotConnected,
                    Diagnostic = resolution.Diagnostic,
                }
            );

        var execution = await bridgeClient.ExecuteCommandAsync(
            target,
            BridgeIdentifiers.CreateRequestId(),
            new()
            {
                CommandType = BridgeCommandTypes.Status,
                TrackUsage = true,
                IncludeBackgroundLogs = true,
            },
            UnityToolTimeouts.StatusCommand,
            processIdHint: null,
            ct
        );
        if (TryParsePingSnapshot(execution, out var snapshot))
            return ToolResponseFormatter.AppendBackgroundLogs(AppendLivePlayers(
                UnityProjectStatusFormatter.FormatPingReport(snapshot),
                execution.Handshake is { IsPlayer: true } handshake
                    ?
                    [
                        new()
                        {
                            ProcessId = handshake.EffectiveProcessId,
                            SessionInstanceId = handshake.SessionInstanceId,
                        },
                    ]
                    : []
            ), execution.Result?.BackgroundLogs);

        return ToolResponseFormatter.Format(
            execution.Result
            ?? ToToolExecutionResult(
                target,
                BridgeCommandTypes.Status,
                execution,
                UnityToolTimeouts.StatusCommand
            )
        );
    }

    static string AppendLivePlayers(
        string report,
        BridgeEndpointDescriptor[] players)
    {
        if (players.Length == 0)
            return report;

        Array.Sort(players, static (left, right) =>
        {
            var processId = left.ProcessId.CompareTo(right.ProcessId);
            return processId != 0
                ? processId
                : string.Compare(
                    left.SessionInstanceId,
                    right.SessionInstanceId,
                    StringComparison.Ordinal
                );
        });

        using var builder = ZString.CreateStringBuilder();
        builder.Append(report.AsSpan().TrimEnd());
        foreach (var player in players)
        {
            builder.Append("\nLive player detected: player:");
            builder.Append(player.ProcessId);
        }

        return builder.ToString();
    }

    async Task<ToolExecutionResult> ExecutePlayerProfilerAsync(
        string playerTarget,
        BridgeCommand command,
        CT ct)
    {
        var selector = PlayerSelector.TryParse(playerTarget, out var parsed)
            ? parsed
            : default;
        var resolution = await playerDiscovery.ResolveAsync(selector, ct);
        if (resolution.Endpoint is not { } endpoint)
            return ToolExecutionResult.NotConnected(
                playerTarget,
                resolution.Diagnostic
            );

        var markerName = "Conduit.Player." + endpoint.SessionInstanceId;
        foreach (var project in projectRegistry.SnapshotProjects())
        {
            if (!Directory.Exists(
                    ProjectPathNormalizer.ToPlatformPath(project.ProjectPath)
                )
                || !UnityProjectIdentity.Read(project.ProjectPath).Matches(endpoint))
                continue;

            var probe = await bridgeClient.ExecuteCommandAsync(
                project.ProjectPath,
                BridgeIdentifiers.CreateRequestId(),
                new()
                {
                    CommandType = BridgeCommandTypes.ProfilerHasMarker,
                    Args = [markerName],
                    TrackUsage = false,
                },
                UnityToolTimeouts.StatusCommand,
                processIdHint: null,
                ct
            );
            if (probe.Handshake is not { } handshake
                || !string.Equals(
                    handshake.UnityVersion,
                    endpoint.UnityVersion,
                    StringComparison.Ordinal
                )
                || probe.Result?.Outcome != ToolOutcome.Success
                || !string.Equals(
                    probe.Result.ReturnValue,
                    "true",
                    StringComparison.OrdinalIgnoreCase
                ))
                continue;

            return await EnqueueAsync(project.ProjectPath, command, ct);
        }

        return new()
        {
            Outcome = ToolOutcome.NotConnected,
            Diagnostic =
                $"No matching Unity Editor is currently profiling {playerTarget}.",
        };
    }
}
