using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed class UnityProjectOperations(
    UnityProjectRegistry projectRegistry,
    UnityPlayerDiscovery playerDiscovery,
    UnityBridgeClient bridgeClient,
    SnippetCompiler snippetCompiler,
    DetourCompiler detourCompiler,
    UnityProjectEnvironmentInspector environmentInspector,
    UnityEditorProcessController processController,
    UnitySceneReloadPromptRecovery sceneReloadPromptRecovery,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory)
{
    static readonly TimeSpan recentReachablePreflightBypassWindow = TimeSpan.FromSeconds(10);
    static readonly Regex assetDatabaseRefreshCallPattern = new(
        @"\b(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)?AssetDatabase\s*\.\s*Refresh\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant
    );

    internal static readonly string AssetDatabaseRefreshDiagnostic =
        "`AssetDatabase.Refresh` should not be called via `execute_code`. Use the `refresh_asset_database` tool instead.";

    internal const string ReflectValidModes =
        "types, classes, structs, enums, interfaces, delegates, members, fields, properties, methods, constructors";

    internal static readonly string ReflectMissingModeDiagnostic =
        $"`reflect` requires a mode. Valid modes: {ReflectValidModes}.";

    readonly ILogger<UnityProjectOperations> logger = loggerFactory.CreateLogger<UnityProjectOperations>();

    readonly ConcurrentDictionary<string, ProjectCommandQueue> queues
        = new(StringComparer.OrdinalIgnoreCase);

    readonly ConcurrentDictionary<string, ProjectSession> playerSessions
        = new(StringComparer.Ordinal);

    readonly ProjectSingleFlight<ToolExecutionResult> restartOperations = new();

    readonly RefreshAssetDatabaseRecoveryCoordinator refreshAssetDatabaseRecoveryCoordinator
        = new(bridgeClient, projectRegistry, environmentInspector, sceneReloadPromptRecovery, loggerFactory.CreateLogger<RefreshAssetDatabaseRecoveryCoordinator>());

    public async Task<string> StatusAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        if (PlayerSelector.TryParse(normalizedProjectPath, out var playerSelector))
            return await StatusPlayerAsync(playerSelector, ct);

        string AppendPlayers(string report) =>
            AppendLivePlayers(report, playerDiscovery.FindForProject(normalizedProjectPath));

        var usage = new StatusUsageState();
        try
        {
            var session = projectRegistry.GetOrAddProject(normalizedProjectPath);
            var optimisticReport = await TryBuildOptimisticStatusReportAsync(
                normalizedProjectPath,
                session,
                usage,
                ct
            );
            if (optimisticReport is { } report)
                return AppendPlayers(report);

            return AppendPlayers(
                await ExecuteStatusWithPreflightAsync(normalizedProjectPath, usage, ct)
            );
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Status failed for project '{ProjectPath}' because an internal timeout or cancellation escaped the normal fallback path.", normalizedProjectPath);
            return AppendPlayers(
                BuildSafeUnexpectedStatusResponse(
                    normalizedProjectPath,
                    "Status probing was cancelled before a response could be formatted."
                )
            );
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Status failed unexpectedly for project '{ProjectPath}'. Falling back to environment diagnostics.", normalizedProjectPath);
            return AppendPlayers(
                BuildSafeUnexpectedStatusResponse(
                    normalizedProjectPath,
                    $"Status probing failed unexpectedly: {exception.Message}"
                )
            );
        }
    }

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
            ConduitUtility.CreateRequestId(),
            new()
            {
                CommandType = BridgeCommandTypes.Status,
                TrackUsage = true,
            },
            UnityToolTimeouts.StatusCommand,
            processIdHint: null,
            ct
        );
        if (TryParsePingSnapshot(execution, out var snapshot))
            return AppendLivePlayers(
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
            );

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

    public Task<ToolExecutionResult> RestartAsync(string projectPath, CT ct)
        => PlayerSelector.TryParse(BridgeTarget.Normalize(projectPath), out _)
            ? EnqueueAsync(
                projectPath,
                new() { CommandType = BridgeCommandTypes.Restart },
                ct
            )
            : RestartAsync(projectPath, trackUsage: true, ct);

    public Task<ToolExecutionResult> HelpAsync(string projectPath, CT ct)
        => EnqueueAsync(
            projectPath,
            new() { CommandType = BridgeCommandTypes.Help },
            ct
        );

    Task<ToolExecutionResult> RestartAsync(string projectPath, bool trackUsage, CT ct)
        => restartOperations.RunAsync(
            projectPath,
            (path, token) => processController.RestartAsync(path, trackUsage, token),
            applicationLifetime.ApplicationStopping,
            ct
        );

    public Task<ToolExecutionResult> EnterPlayModeAsync(string projectPath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.PlayMode },
            ct: ct
        );

    public Task<ToolExecutionResult> EnterEditModeAsync(string projectPath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.EditMode },
            ct: ct
        );

    public Task<ToolExecutionResult> ScreenshotAsync(string projectPath, string target, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Screenshot, Target = target },
            ct: ct
        );

    public Task<ToolExecutionResult> GetDependenciesAsync(string projectPath, string asset, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.GetDependencies,
                Target = asset,
            },
            ct: ct
        );

    public Task<ToolExecutionResult> FindReferencesToAsync(string projectPath, string asset, bool rebuildCache, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.FindReferencesTo,
                Target = asset,
                RebuildCache = rebuildCache,
            },
            ct: ct
        );

    public Task<ToolExecutionResult> FindMissingScriptsAsync(string projectPath, string assetPattern, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.FindMissingScripts, Target = assetPattern },
            ct: ct
        );

    public Task<ToolExecutionResult> ShowAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Show, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> SearchAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.Search, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> ToJsonAsync(string projectPath, string query, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ToJson, Target = query },
            ct: ct
        );

    public Task<ToolExecutionResult> FromJsonOverwriteAsync(string projectPath, string query, string json, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.FromJsonOverwrite, Target = query, Snippet = json },
            ct: ct
        );

    public Task<ToolExecutionResult> SaveScenesAsync(string projectPath, string? scenePath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.SaveScenes, Target = scenePath },
            ct: ct
        );

    public Task<ToolExecutionResult> DiscardScenesAsync(string projectPath, string? scenePath, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.DiscardScenes, Target = scenePath },
            ct: ct
        );

    public async Task<ToolExecutionResult> RefreshAssetDatabaseAsync(string projectPath, CT ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        return await EnqueueAsync(
            projectPath: normalizedProjectPath,
            command: new() { CommandType = BridgeCommandTypes.RefreshAssetDatabase },
            ct: ct
        );
    }

    public async Task<ToolExecutionResult> ReimportAssetsAsync(string projectPath, string query, CT ct)
    {
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        return await EnqueueAsync(
            projectPath: normalizedProjectPath,
            command: new() { CommandType = BridgeCommandTypes.ReimportAssets, Target = query },
            ct: ct
        );
    }

    public Task<ToolExecutionResult> ExecuteCodeAsync(string projectPath, string snippet, CT ct)
    {
        if (CallsAssetDatabaseRefresh(snippet))
        {
            return Task.FromResult(
                new ToolExecutionResult
                {
                    Outcome = ToolOutcome.Exception,
                    Diagnostic = AssetDatabaseRefreshDiagnostic,
                }
            );
        }

        return ExecuteCompiledCodeAsync(BridgeTarget.Normalize(projectPath), snippet, ct);
    }

    async Task<ToolExecutionResult> ExecuteCompiledCodeAsync(
        string target,
        string snippet,
        CT ct)
    {
        var prepared = await snippetCompiler.CompileAsync(target, snippet, ct);
        if (prepared.Failure is { } failure)
            return failure;

        var compilation = prepared.Compilation!;
        return await DispatchCompiledCommandAsync(
            target,
            compilation.ToCommand(),
            compilation.Warning,
            "The MCP server could not stage the compiled player snippet.",
            ct
        );
    }

    internal static bool CallsAssetDatabaseRefresh(string? snippet) =>
        !string.IsNullOrEmpty(snippet) && assetDatabaseRefreshCallPattern.IsMatch(snippet);

    public async Task<ToolExecutionResult> DetourAsync(
        string projectPath,
        string methodName,
        string replacementBody,
        CT ct)
    {
        var target = BridgeTarget.Normalize(projectPath);
        var prepared = await detourCompiler.PrepareAsync(target, methodName, replacementBody, ct);
        if (prepared.Failure is { } failure)
            return failure;

        return await DispatchCompiledCommandAsync(
            target,
            prepared.Command!,
            prepared.Warning,
            "The MCP server could not stage the compiled detour.",
            ct
        );
    }

    async Task<ToolExecutionResult> DispatchCompiledCommandAsync(
        string target,
        BridgeCommand command,
        string? warning,
        string stagingFailureDiagnostic,
        CT ct)
    {
        ToolExecutionResult result;
        if (!PlayerSelector.TryParse(target, out var selector))
            result = await EnqueueAsync(target, command, ct);
        else
        {
            var resolution = await playerDiscovery.ResolveAsync(selector, ct);
            if (resolution.Endpoint is not { } endpoint)
                return new()
                {
                    Outcome = ToolOutcome.NotConnected,
                    Diagnostic = resolution.Diagnostic,
                };

            try
            {
                // editor artifacts are shared project files; remote players need endpoint-local files.
                command.Artifacts = PlayerArtifactStore.Materialize(endpoint, command.Artifacts);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    stagingFailureDiagnostic
                );
            }

            result = await EnqueuePlayerAsync(target, command, ct);
        }

        if (string.IsNullOrWhiteSpace(warning) || result.Outcome != ToolOutcome.Success)
            return result;

        return new()
        {
            Outcome = result.Outcome,
            DisplayName = result.DisplayName,
            Logs = result.Logs,
            ReturnValue = result.ReturnValue,
            Exception = result.Exception,
            Diagnostic = warning,
        };
    }

    public Task<ToolExecutionResult> ViewBurstAsmAsync(string projectPath, string target, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ViewBurstAsm, Target = target },
            ct: ct
        );

    public Task<ToolExecutionResult> ReflectAsync(string projectPath, string mode, string? type, string? member, CT ct)
    {
        if (ValidateReflectRequest(mode) is { } validationResult)
            return Task.FromResult(validationResult);

        return EnqueueAsync(
            projectPath: projectPath,
            command: new()
            {
                CommandType = BridgeCommandTypes.Reflect,
                Args = [mode, type ?? string.Empty, member ?? string.Empty],
            },
            ct: ct
        );
    }

    internal static ToolExecutionResult? ValidateReflectRequest(string mode)
        => string.IsNullOrWhiteSpace(mode)
            ? new()
            {
                Outcome = ToolOutcome.Exception,
                Diagnostic = ReflectMissingModeDiagnostic,
            }
            : null;

    public Task<ToolExecutionResult> RunTestsEditModeAsync(string projectPath, string? testFilter, bool @async, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsEditMode,
                TestFilter = testFilter,
                Async = @async ? true : null,
            },
            ct
        );

    public Task<ToolExecutionResult> RunTestsPlayModeAsync(string projectPath, string? testFilter, bool @async, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsPlayMode,
                TestFilter = testFilter,
                Async = @async ? true : null,
            },
            ct
        );

    public Task<ToolExecutionResult> RunTestsPlayerAsync(string projectPath, string? testFilter, CT ct)
        => EnqueueAsync(
            projectPath,
            new()
            {
                CommandType = BridgeCommandTypes.RunTestsPlayer,
                TestFilter = testFilter,
            },
            ct
        );

    public Task<ToolExecutionResult> ProfilerRecordAsync(
        string projectPath,
        ProfilerRecordAction action,
        int frames,
        double delaySeconds,
        string target,
        string? fileName,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.ProfilerRecord,
            Args = BuildProfilerRecordArgs(action, frames, delaySeconds, target, fileName),
        },
        ct
    );

    internal static string[] BuildProfilerRecordArgs(
        ProfilerRecordAction action,
        int frames,
        double delaySeconds,
        string target,
        string? fileName
    ) =>
    [
        $"action={action.ToWireName()}",
        $"frames={frames}",
        $"delay_seconds={delaySeconds.ToString(CultureInfo.InvariantCulture)}",
        $"target={target}",
        $"file_name={fileName ?? string.Empty}",
    ];

    public Task<ToolExecutionResult> ProfilerOverviewAsync(
        string projectPath,
        ProfilerOverviewMode mode,
        string frameRange,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.ProfilerOverview,
            Args =
            [
                $"mode={mode.ToWireName()}",
                $"frame_range={frameRange}",
            ],
        },
        ct
    );

    public Task<ToolExecutionResult> ProfilerBrowseAsync(
        string projectPath,
        string frame,
        string thread,
        string root,
        int depth,
        ProfilerBrowseSort sort,
        int limit,
        bool onlyNonTrivial,
        CT ct
    ) => EnqueueAsync(
        projectPath,
        new()
        {
            CommandType = BridgeCommandTypes.ProfilerBrowse,
            Args =
            [
                $"frame={frame}",
                $"thread={thread}",
                $"root={root}",
                $"depth={depth}",
                $"sort={sort.ToWireName()}",
                $"limit={limit}",
                $"only_non_trivial={onlyNonTrivial.ToString().ToLowerInvariant()}",
            ],
        },
        ct
    );

    async Task<ToolExecutionResult> EnqueueAsync(string projectPath, BridgeCommand command, CT ct)
    {
        command.TrackUsage = true;
        var normalizedProjectPath = BridgeTarget.Normalize(projectPath);
        if (PlayerSelector.TryParse(normalizedProjectPath, out _))
        {
            if (BridgeCommandKinds.IsProfiler(
                    BridgeCommandKinds.Parse(command.CommandType)
                ))
                return await ExecutePlayerProfilerAsync(
                    normalizedProjectPath,
                    command,
                    ct
                );

            return await EnqueuePlayerAsync(normalizedProjectPath, command, ct);
        }

        var session = projectRegistry.GetOrAddProject(normalizedProjectPath);
        var blockedResult = await TryPrepareProjectAsync(normalizedProjectPath, session, command.CommandType, ct);
        if (blockedResult is { } preparationResult)
            return preparationResult;

        var queue = queues.GetOrAdd(
            session.ProjectPath,
            _ => new(
                loggerFactory.CreateLogger<ProjectCommandQueue>(),
                ExecuteQueuedCommandAsync,
                applicationLifetime.ApplicationStopping
            )
        );

        var commandTimeout = UnityToolTimeouts.ForCommand(BridgeCommandKinds.Parse(command.CommandType));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(commandTimeout);

        var result = await queue.EnqueueAsync(new(session, command, timeoutCts.Token), timeoutCts.Token);
        if (!ct.IsCancellationRequested
            && timeoutCts.IsCancellationRequested
            && result.Outcome == ToolOutcome.Cancelled)
            return ToolExecutionResult.Timeout(
                commandTimeout,
                $"Unity did not start or finish '{command.CommandType}' within {commandTimeout}."
            );

        return result;
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
                ConduitUtility.CreateRequestId(),
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

    async Task<ToolExecutionResult> EnqueuePlayerAsync(
        string playerTarget,
        BridgeCommand command,
        CT ct)
    {
        var selector = PlayerSelector.TryParse(playerTarget, out var parsedSelector)
            ? parsedSelector
            : throw new InvalidOperationException($"Player target '{playerTarget}' is invalid.");
        var resolution = await playerDiscovery.ResolveAsync(selector, ct);
        if (resolution.Endpoint is null)
            return new()
            {
                Outcome = ToolOutcome.NotConnected,
                Diagnostic = resolution.Diagnostic,
            };

        var session = playerSessions.GetOrAdd(playerTarget, static target => new(target));
        var queue = queues.GetOrAdd(
            playerTarget,
            _ => new(
                loggerFactory.CreateLogger<ProjectCommandQueue>(),
                ExecuteQueuedPlayerCommandAsync,
                applicationLifetime.ApplicationStopping
            )
        );
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

    async Task<ToolExecutionResult> CompletePlayerRestartAsync(
        ToolExecutionResult result,
        CT ct)
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

    /*
     * Healthy editors already have a live bridge connection. Try the cheap Unity-side
     * status call first and only pay for offline diagnostics when that fast path fails
     * to produce a real status payload.
     */
    async Task<string?> TryBuildOptimisticStatusReportAsync(
        string normalizedProjectPath,
        ProjectSession session,
        StatusUsageState usage,
        CT ct
    )
    {
        if (!TrySkipOfflinePreflight(session, normalizedProjectPath, out var cachedHandshake))
            return null;

        var execution = await ExecuteRecoverableStatusCommandAsync(
            normalizedProjectPath,
            cachedHandshake?.EditorProcessId,
            UnityToolTimeouts.StatusCommand,
            usage,
            ct
        );

        if (!TryParsePingSnapshot(execution, out var pingSnapshot))
            return null;

        var report = BuildPingReport(normalizedProjectPath, pingSnapshot);

        await UpdateProjectRegistryAsync(normalizedProjectPath, execution.Handshake, ct);
        return report;
    }

    async Task<string> ExecuteStatusWithPreflightAsync(
        string normalizedProjectPath,
        StatusUsageState usage,
        CT ct
    )
    {
        var preflight = await UnityProjectOfflinePreflight.ExecuteAsync(
            normalizedProjectPath,
            environmentInspector,
            projectRegistry,
            bridgeClient,
            UnityToolTimeouts.StatusWithoutKnownProcess,
            ct
        );

        var timeout = preflight.Snapshot.MatchedProcess is null
            ? UnityToolTimeouts.StatusWithoutKnownProcess
            : UnityToolTimeouts.StatusCommand;

        if (preflight.IsBlocked)
        {
            if (ShouldWaitForBlockedStatusProgressWindow(preflight.Snapshot, preflight.Diagnostic, preflight.ProbeExecution)
                && await TryWaitForStatusProgressWindowAsync(
                    normalizedProjectPath,
                    preflight.Snapshot,
                    preflight.ProbeExecution,
                    timeout,
                    usage,
                    ct
                )
                is { } progressExecution)
            {
                await UpdateProjectRegistryAsync(normalizedProjectPath, progressExecution.Handshake, ct);
                return BuildStatusResponse(normalizedProjectPath, progressExecution, preflight.Snapshot, timeout);
            }

            return environmentInspector.FormatPingFailure(
                preflight.Snapshot,
                ToolExecutionResult.NotConnected(normalizedProjectPath, preflight.Diagnostic)
            );
        }

        var execution = ShouldUseProbeExecutionForStatus(preflight.ProbeExecution)
            ? preflight.ProbeExecution!
            : await ExecuteRecoverableStatusCommandAsync(
                normalizedProjectPath,
                preflight.Snapshot.MatchedProcess?.ProcessId,
                timeout,
                usage,
                ct
            );

        if (await TryWaitForStatusProgressWindowAsync(
                normalizedProjectPath,
                preflight.Snapshot,
                execution,
                timeout,
                usage,
                ct
            )
            is { } recoveredExecution)
            execution = recoveredExecution;

        await UpdateProjectRegistryAsync(normalizedProjectPath, execution.Handshake, ct);
        return BuildStatusResponse(normalizedProjectPath, execution, preflight.Snapshot, timeout);
    }

    /*
     * Commands can trust the cached bridge only while it is live. Otherwise fall back
     * to the normal preflight path so failure diagnostics stay accurate.
     */
    async Task<ToolExecutionResult?> TryPrepareProjectAsync(string normalizedProjectPath, ProjectSession session, string commandType, CT ct)
    {
        if (TrySkipOfflinePreflight(session, normalizedProjectPath, out var cachedHandshake))
        {
            if (cachedHandshake is not null)
                await UpdateProjectRegistryAsync(normalizedProjectPath, cachedHandshake, ct);

            return null;
        }

        var preflight = await UnityProjectOfflinePreflight.ExecuteAsync(
            normalizedProjectPath,
            environmentInspector,
            projectRegistry,
            bridgeClient,
            UnityToolTimeouts.StatusWithoutKnownProcess,
            ct
        );

        if (preflight.IsBlocked)
            return ToolExecutionResult.NotConnected(
                normalizedProjectPath,
                FormatBlockedDiagnosticForCommand(commandType, preflight.Diagnostic)
            );

        await UpdateProjectRegistryAsync(normalizedProjectPath, preflight.ProbeExecution?.Handshake, ct);
        return null;
    }

    async Task<ToolExecutionResult> ExecuteQueuedCommandAsync(QueuedProjectCommand queuedCommand, CT ct)
    {
        var context = queuedCommand.Session.StartCommand(queuedCommand.Command);
        var commandKind = BridgeCommandKinds.Parse(queuedCommand.Command.CommandType);
        var commandTimeout = UnityToolTimeouts.ForCommand(commandKind);
        // tests have a real cancellation API; other editor side effects retain reconnect-and-replay semantics.
        var commandCancellation = BridgeCommandKinds.IsTest(commandKind)
            ? queuedCommand.RequestCancellation
            : default;
        var reachable = false;
        int? monitoredProcessId = null;

        try
        {
            if (BridgeCommandKinds.IsAssetImport(commandKind))
            {
                monitoredProcessId = environmentInspector
                    .Inspect(queuedCommand.Session.ProjectPath)
                    .MatchedProcess
                    ?.ProcessId;

                var recovery = await refreshAssetDatabaseRecoveryCoordinator.ExecuteAsync(
                    queuedCommand.Session.ProjectPath,
                    context.RequestId,
                    queuedCommand.Command,
                    monitoredProcessId,
                    UnityToolTimeouts.RefreshAssetDatabaseActivation,
                    UnityToolTimeouts.StatusCommand,
                    commandTimeout,
                    UnityToolTimeouts.RefreshAssetDatabaseRecoveryPollInterval,
                    ct
                );

                monitoredProcessId = recovery.MonitoredProcessId;
                reachable = recovery.Reachable;
                return recovery.Result;
            }

            var execution = await ExecuteReplayableCommandAsync(commandTimeout);
            await RecoverTimedOutTestCommandAsync(commandKind, queuedCommand.Session.ProjectPath, execution);
            var result = execution.Result
                         ?? ToToolExecutionResult(
                             queuedCommand.Session.ProjectPath,
                             queuedCommand.Command.CommandType,
                             execution,
                             commandTimeout,
                             environmentInspector
                         );
            if (commandKind == BridgeCommandKind.RunTestsPlayer)
                await ShutdownTestPlayersAsync(queuedCommand.Session.ProjectPath, result);
            return result;
        }
        finally
        {
            queuedCommand.Session.FinishCommand(context.RequestId, reachable);
        }

        async Task<BridgeClientResult> ExecuteReplayableCommandAsync(TimeSpan timeout)
        {
            var execution = await bridgeClient.ExecuteCommandAsync(
                queuedCommand.Session.ProjectPath,
                context.RequestId,
                queuedCommand.Command,
                timeout,
                monitoredProcessId,
                ct,
                commandCancellation
            );

            await ApplyHandshakeAsync(execution);
            if (!ShouldReplayRequest(execution))
                return execution;

            // recover the common case where unity accepted the command but is blocked in a native reload prompt.
            await sceneReloadPromptRecovery.TryDismissAsync(
                queuedCommand.Session.ProjectPath,
                monitoredProcessId ?? execution.Handshake?.EditorProcessId,
                ct
            );

            var retriedExecution = await bridgeClient.ExecuteCommandAsync(
                queuedCommand.Session.ProjectPath,
                context.RequestId,
                queuedCommand.Command,
                timeout,
                monitoredProcessId,
                ct,
                commandCancellation
            );

            await ApplyHandshakeAsync(retriedExecution);
            return retriedExecution;
        }

        async Task ApplyHandshakeAsync(BridgeClientResult execution)
        {
            if (execution.Handshake is not { } handshake)
                return;

            reachable = execution.FailureKind != BridgeRuntimeFailureKind.ProcessExited;
            monitoredProcessId = handshake.EditorProcessId > 0 ? handshake.EditorProcessId : monitoredProcessId;
            await projectRegistry.UpdateFromHandshakeAsync(handshake, ct);
        }

        async Task RecoverTimedOutTestCommandAsync(BridgeCommandKind currentCommandKind, string projectPath, BridgeClientResult execution)
        {
            if (!ShouldRecoverTimedOutTestCommand(currentCommandKind, execution))
                return;

            logger.LogWarning(
                "Unity test command '{CommandType}' timed out for project {ProjectPath}. Starting automatic editor recovery.",
                queuedCommand.Command.CommandType,
                projectPath
            );

            using var recoveryCts = CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
            recoveryCts.CancelAfter(UnityToolTimeouts.RestartStartupMax);

            try
            {
                var recoveryResult = await RestartAsync(
                    projectPath,
                    trackUsage: false,
                    recoveryCts.Token
                );
                logger.LogInformation(
                    "Automatic recovery for timed out Unity test command '{CommandType}' completed with outcome {Outcome}.",
                    queuedCommand.Command.CommandType,
                    recoveryResult.Outcome
                );
            }
            catch (OperationCanceledException) when (recoveryCts.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Automatic recovery for timed out Unity test command '{CommandType}' timed out or was cancelled.",
                    queuedCommand.Command.CommandType
                );
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Automatic recovery for timed out Unity test command '{CommandType}' failed.",
                    queuedCommand.Command.CommandType
                );
            }
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
                    ConduitUtility.CreateRequestId(),
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
            while (players.Any(IsLive))
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, shutdownCts.Token);
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested) { }

        foreach (var player in players.Where(IsLive))
            failures.Add($"{player.Selector}: the player did not exit within 15 seconds");

        if (failures.Count > 0)
            testResult.Diagnostic = string.Join(
                "\n",
                new[] { testResult.Diagnostic, "Player shutdown failed: " + string.Join("; ", failures) }
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
            );

        bool IsLive(BridgeEndpointDescriptor endpoint) => playerDiscovery.Discover()
            .Any(candidate => string.Equals(
                candidate.SessionInstanceId,
                endpoint.SessionInstanceId,
                StringComparison.Ordinal
            ));
    }

    internal static ToolExecutionResult ToToolExecutionResult(
        string projectPath,
        string commandType,
        BridgeClientResult execution,
        TimeSpan timeout
    )
    {
        if (execution.Result is { } result)
            return result;

        if (execution.FailureKind is null)
            return ToolExecutionResult.NotConnected(projectPath);

        var diagnostic = string.IsNullOrWhiteSpace(execution.FailureDiagnostic)
            ? $"Unity did not complete '{commandType}'."
            : execution.FailureDiagnostic;

        return execution.FailureKind is BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultTimedOut
            ? ToolExecutionResult.Timeout(timeout, diagnostic)
            : ToolExecutionResult.NotConnected(projectPath, diagnostic);
    }

    internal static ToolExecutionResult ToToolExecutionResult(
        string projectPath,
        string commandType,
        BridgeClientResult execution,
        TimeSpan timeout,
        UnityProjectEnvironmentInspector environmentInspector,
        UnityProjectEnvironmentSnapshot? snapshot = null
    )
    {
        var fallback = ToToolExecutionResult(projectPath, commandType, execution, timeout);
        if (execution.Result is not null || execution.Handshake is not null || execution.FailureKind is null)
            return fallback;

        var diagnostic = UnityProjectOfflinePreflight.ResolveBlockedDiagnostic(
            environmentInspector,
            projectPath,
            execution,
            snapshot
        );

        if (string.IsNullOrWhiteSpace(diagnostic))
            return fallback;

        diagnostic = FormatBlockedDiagnosticForCommand(commandType, diagnostic);
        if (diagnostic == fallback.Diagnostic)
            return fallback;

        return fallback.Outcome == ToolOutcome.Timeout
            ? ToolExecutionResult.Timeout(timeout, diagnostic)
            : ToolExecutionResult.NotConnected(projectPath, diagnostic);
    }

    internal static string FormatBlockedDiagnosticForCommand(string commandType, string diagnostic) =>
        commandType == BridgeCommandTypes.RefreshAssetDatabase
        && diagnostic == UnityProjectEnvironmentProbe.SafeModeDiagnostic
            ? UnityProjectEnvironmentProbe.RefreshAssetDatabaseSafeModeDiagnostic
            : diagnostic;

    public static bool ShouldReplayRequest(BridgeClientResult execution) =>
        execution.Handshake is not null
        && execution.FailureKind is BridgeRuntimeFailureKind.SendFailed
            or BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckDisconnected
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultDisconnected
            or BridgeRuntimeFailureKind.ResultTimedOut;

    internal static bool ShouldRecoverTimedOutTestCommand(BridgeCommandKind currentCommandKind, BridgeClientResult execution) =>
        BridgeCommandKinds.IsTest(currentCommandKind)
        && execution.Handshake is not null
        && (execution.FailureKind is BridgeRuntimeFailureKind.SendTimedOut
            or BridgeRuntimeFailureKind.StartAckTimedOut
            or BridgeRuntimeFailureKind.ResultTimedOut);

    public static bool ShouldReportReachableStatus(BridgeClientResult execution) =>
        execution.Handshake is not null
        && execution.FailureKind is not (
            BridgeRuntimeFailureKind.ProcessExited
            or BridgeRuntimeFailureKind.SendFailed
            or BridgeRuntimeFailureKind.StartAckDisconnected
            );

    internal static bool ShouldUseProbeExecutionForStatus(BridgeClientResult? probeExecution)
        => probeExecution?.Result is not null;

    internal static bool ShouldWaitForBlockedStatusProgressWindow(
        UnityProjectEnvironmentSnapshot snapshot,
        string diagnostic,
        BridgeClientResult? execution
    ) =>
        !IsTerminalOfflineDiagnostic(diagnostic)
        && ShouldWaitForStatusProgressWindow(snapshot, execution);

    internal static bool ShouldWaitForStatusProgressWindow(UnityProjectEnvironmentSnapshot snapshot, BridgeClientResult? execution)
    {
        if (snapshot.MatchedProcess is null || execution is null)
            return false;

        if (execution.Result?.Outcome == ToolOutcome.Success)
            return false;

        if (execution.FailureKind is null)
            return execution.Result?.Outcome is ToolOutcome.Timeout or ToolOutcome.NotConnected;

        return execution.FailureKind is not BridgeRuntimeFailureKind.ProcessExited
            and not BridgeRuntimeFailureKind.ProtocolMismatch
            and not BridgeRuntimeFailureKind.ProjectMismatch;
    }

    internal static TimeSpan GetStatusProgressTitleChangeWindow(int completedTitleChangeExtensions) =>
        completedTitleChangeExtensions <= 0
            ? UnityToolTimeouts.StatusProgressFirstTitleChangeWindow
            : UnityToolTimeouts.StatusProgressTitleChangeWindow;

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
        if (!ShouldWaitForStatusProgressWindow(snapshot, latestExecution)
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

            if (!ShouldWaitForStatusProgressWindow(snapshot, lastExecution))
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

                var extension = GetStatusProgressTitleChangeWindow(completedTitleChangeExtensions++);
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

    static bool IsTerminalOfflineDiagnostic(string diagnostic) =>
        diagnostic == UnityProjectOfflinePreflight.InvalidProjectDiagnostic
        || diagnostic == UnityProjectOfflinePreflight.MissingPackageDiagnostic
        || diagnostic == UnityProjectOfflinePreflight.OfflineDiagnostic
        || diagnostic == UnityProjectEnvironmentProbe.SafeModeDiagnostic
        || diagnostic == UnityProjectEnvironmentProbe.RefreshAssetDatabaseSafeModeDiagnostic;

    static string? TryReadProgressWindowTitle(int processId) =>
        UnityWindowTitleProbe
            .TryFindMatchingProcessWindowTitle(processId, UnityWindowTitleClassifier.IsProgressTitle)
            ?.Title;

    async Task<BridgeClientResult> ExecuteRecoverableStatusCommandAsync(
        string normalizedProjectPath,
        int? processIdHint,
        TimeSpan timeout,
        StatusUsageState usage,
        CT ct
    )
    {
        var requestId = ConduitUtility.CreateRequestId();
        var execution = await ExecuteAsync();

        if (!ShouldReplayRequest(execution))
            return execution;

        return await ExecuteAsync();

        async Task<BridgeClientResult> ExecuteAsync()
        {
            bool trackUsage = !usage.WasSent;
            var result = await bridgeClient.ExecuteCommandAsync(
                normalizedProjectPath,
                requestId,
                new()
                {
                    CommandType = BridgeCommandTypes.Status,
                    TrackUsage = trackUsage,
                },
                timeout,
                processIdHint,
                ct
            );
            usage.WasSent |= trackUsage && result.CommandSent;
            return result;
        }
    }

    async Task UpdateProjectRegistryAsync(string normalizedProjectPath, BridgeProjectHandshake? handshake, CT ct)
    {
        if (handshake is { } projectHandshake)
            await projectRegistry.UpdateFromHandshakeAsync(projectHandshake, ct);
        else
            projectRegistry.MarkReachable(normalizedProjectPath, false);
    }

    string BuildStatusResponse(string normalizedProjectPath, BridgeClientResult execution, UnityProjectEnvironmentSnapshot? snapshot = null, TimeSpan? statusTimeout = null)
    {
        if (TryParsePingSnapshot(execution, out var pingSnapshot))
            return BuildPingReport(normalizedProjectPath, pingSnapshot, snapshot);

        var currentSnapshot = snapshot ?? environmentInspector.Inspect(normalizedProjectPath);
        var effectiveHandshake = execution.Handshake;
        var processRuntime = environmentInspector.TryReadProcessRuntime(
            environmentInspector.ResolveEditorProcessId(currentSnapshot, effectiveHandshake)
        );
        var compilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(currentSnapshot);

        if (execution.FailureKind is not null)
        {
            if (!ShouldReportReachableStatus(execution))
                return environmentInspector.FormatPingFailure(
                    currentSnapshot,
                    ToToolExecutionResult(
                        normalizedProjectPath,
                        BridgeCommandTypes.Status,
                        execution,
                        statusTimeout ?? UnityToolTimeouts.StatusCommand,
                        environmentInspector,
                        currentSnapshot
                    )
                );

            return environmentInspector.FormatPingReachable(
                currentSnapshot,
                effectiveHandshake!,
                processRuntime,
                compilationDiagnostics,
                execution.FailureDiagnostic ?? string.Empty
            );
        }

        if (execution.Result is { } result)
        {
            if (effectiveHandshake is not null && result.Outcome is (ToolOutcome.Timeout or ToolOutcome.NotConnected))
                return environmentInspector.FormatPingReachable(
                    currentSnapshot,
                    effectiveHandshake,
                    processRuntime,
                    compilationDiagnostics,
                    result.Diagnostic ?? $"Connected to the bridge, but Unity did not complete a status command within {statusTimeout ?? UnityToolTimeouts.StatusCommand}."
                );

            return environmentInspector.FormatPingFailure(currentSnapshot, result);
        }

        return effectiveHandshake is null
            ? environmentInspector.FormatPingFailure(
                currentSnapshot,
                ToolExecutionResult.NotConnected(normalizedProjectPath, "Unity returned an empty status payload before the bridge handshake completed.")
            )
            : environmentInspector.FormatPingReachable(
                currentSnapshot,
                effectiveHandshake,
                processRuntime,
                compilationDiagnostics,
                "Unity returned an empty status payload."
            );
    }

    string BuildUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic)
    {
        var snapshot = environmentInspector.Inspect(normalizedProjectPath);
        if (bridgeClient.TryGetLiveHandshake(normalizedProjectPath, out var liveHandshake) && liveHandshake is not null)
        {
            var processRuntime = environmentInspector.TryReadProcessRuntime(
                environmentInspector.ResolveEditorProcessId(snapshot, liveHandshake)
            );
            var compilationDiagnostics = environmentInspector.ReadLatestCompilationDiagnostics(snapshot);
            return environmentInspector.FormatPingReachable(
                snapshot,
                liveHandshake,
                processRuntime,
                compilationDiagnostics,
                diagnostic
            );
        }

        return environmentInspector.FormatPingFailure(
            snapshot,
            BuildUnexpectedStatusFailureResult(
                normalizedProjectPath,
                snapshot,
                environmentInspector.HasConduitPackageSignal(normalizedProjectPath),
                diagnostic
            )
        );
    }

    internal static ToolExecutionResult BuildUnexpectedStatusFailureResult(
        string normalizedProjectPath,
        UnityProjectEnvironmentSnapshot snapshot,
        bool hasConduitPackageSignal,
        string diagnostic
    )
    {
        var effectiveDiagnostic = diagnostic;
        if (snapshot.MatchedProcess is not null && hasConduitPackageSignal)
            effectiveDiagnostic = $"{UnityProjectOfflinePreflight.UnresponsiveBridgeDiagnostic} {diagnostic}";
        else if (snapshot.IsUnityProject && !hasConduitPackageSignal)
            effectiveDiagnostic = $"{UnityProjectOfflinePreflight.MissingPackageDiagnostic} {diagnostic}";

        return ToolExecutionResult.NotConnected(normalizedProjectPath, effectiveDiagnostic);
    }

    string BuildSafeUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic)
    {
        try
        {
            return BuildUnexpectedStatusResponse(normalizedProjectPath, diagnostic);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Status fallback failed for project '{ProjectPath}'. Returning a minimal diagnostic.", normalizedProjectPath);
            return BuildMinimalUnexpectedStatusResponse(normalizedProjectPath, diagnostic);
        }
    }

    static string BuildMinimalUnexpectedStatusResponse(string normalizedProjectPath, string diagnostic) =>
        $"Project: {normalizedProjectPath}\nBridge: unreachable\nDiagnostic: {diagnostic}";

    static string AppendLivePlayers(
        string report,
        IReadOnlyCollection<BridgeEndpointDescriptor> players)
    {
        if (players.Count == 0)
            return report;

        var builder = new System.Text.StringBuilder(report.TrimEnd());
        foreach (var player in players
                     .OrderBy(static value => value.ProcessId)
                     .ThenBy(static value => value.SessionInstanceId, StringComparer.Ordinal))
        {
            builder.Append("\nLIVE PLAYER PROCESS ID: `")
                .Append(PlayerSelector.Format(player.ProcessId))
                .Append('`');
        }

        return builder.ToString();
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
            File.WriteAllBytes(path, artifact.Decode());

            return new()
            {
                Outcome = result.Outcome,
                DisplayName = result.DisplayName,
                Logs = result.Logs,
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
                "The player screenshot was received but could not be stored by the MCP server."
            );
        }
    }

    bool TrySkipOfflinePreflight(ProjectSession session, string normalizedProjectPath, out BridgeProjectHandshake? cachedHandshake)
    {
        if (bridgeClient.TryGetLiveHandshake(normalizedProjectPath, out cachedHandshake))
            return true;

        cachedHandshake = null;
        return session.WasReachableRecently(DateTimeOffset.UtcNow, recentReachablePreflightBypassWindow);
    }

    static bool TryParsePingSnapshot(BridgeClientResult execution, out UnityPingSnapshot pingSnapshot)
    {
        if (execution.Result?.Outcome == ToolOutcome.Success
            && !string.IsNullOrWhiteSpace(execution.Result.ReturnValue)
            && UnityPingSnapshotParser.TryParse(execution.Result.ReturnValue, out var parsedSnapshot))
        {
            pingSnapshot = parsedSnapshot;
            return true;
        }

        pingSnapshot = new();
        return false;
    }

    string BuildPingReport(
        string normalizedProjectPath,
        UnityPingSnapshot pingSnapshot,
        UnityProjectEnvironmentSnapshot? snapshot = null
    )
    {
        environmentInspector.RememberEditorLogPath(
            normalizedProjectPath,
            pingSnapshot.EditorLogPath,
            pingSnapshot.EditorProcessId
        );

        var fallbackEditorLogPath = string.IsNullOrWhiteSpace(pingSnapshot.EditorLogPath)
            ? environmentInspector.ResolveEditorLogPath(
                snapshot ?? environmentInspector.Inspect(normalizedProjectPath)
            )
            : null;

        return environmentInspector.FormatPingReport(pingSnapshot, fallbackEditorLogPath);
    }

    // one MCP status call may poll Unity repeatedly; only its first delivered command is usage.
    sealed class StatusUsageState
    {
        internal bool WasSent;
    }
}
