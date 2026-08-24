using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations(
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

    readonly Lock queueCreationGate = new();

    readonly ConcurrentDictionary<string, ProjectSession> playerSessions
        = new(StringComparer.Ordinal);

    readonly ProjectSingleFlight<ToolExecutionResult> restartOperations = new();

    readonly RefreshAssetDatabaseRecoveryCoordinator refreshAssetDatabaseRecoveryCoordinator
        = new(bridgeClient, projectRegistry, environmentInspector, sceneReloadPromptRecovery, loggerFactory.CreateLogger<RefreshAssetDatabaseRecoveryCoordinator>());

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
}
