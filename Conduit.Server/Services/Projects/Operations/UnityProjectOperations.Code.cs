using CT = System.Threading.CancellationToken;

namespace Conduit;

public sealed partial class UnityProjectOperations
{
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

    public Task<ToolExecutionResult> ViewBurstAsmAsync(string projectPath, string target, string cpu, CT ct)
        => EnqueueAsync(
            projectPath: projectPath,
            command: new() { CommandType = BridgeCommandTypes.ViewBurstAsm, Target = target, Args = [cpu] },
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
}
