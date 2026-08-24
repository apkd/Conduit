using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Conduit;

/// <summary>Resolves a loaded Mono method and compiles an ABI-identical replacement.</summary>
public sealed class DetourCompiler(SnippetCompiler snippetCompiler)
{
    readonly ConcurrentDictionary<string, DetourSessionCache> sessionCaches = new(StringComparer.Ordinal);

    internal async Task<PreparedDetour> PrepareAsync(
        string target,
        string selector,
        string replacementBody,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return PreparedDetour.Failed(ExceptionResult("`methodName` must identify a method."));
        if (replacementBody is null)
            return PreparedDetour.Failed(ExceptionResult("`replacementBody` must contain C# code, `test`, or `restore`."));

        var references = await snippetCompiler.GetReferencePathsAsync(target, ct);
        if (references.Failure is { } referenceFailure)
            return PreparedDetour.Failed(referenceFailure);

        var sessionCache = GetSessionCache(
            target,
            references.SessionInstanceId,
            references.References!,
            references.Paths!
        );
        var resolution = sessionCache.GetMethodCatalog().Resolve(selector);
        if (resolution.Target is not { } method)
            return PreparedDetour.Failed(
                new()
                {
                    Outcome = resolution.Outcome ?? ToolOutcome.Exception,
                    Diagnostic = resolution.Diagnostic,
                }
            );

        if (replacementBody == "restore")
            return PreparedDetour.Succeeded(BuildCommand(method, "restore"));

        SourceArtifact artifact;
        bool probe = replacementBody == "test";
        if (probe)
            artifact = new("test", "test.cs", DetourSourceBuilder.BuildProbeBody(method.ReturnType));
        else
        {
            if (replacementBody.Length == 0)
                return PreparedDetour.Failed(ExceptionResult("`replacementBody` cannot be empty."));

            var preparedArtifact = await snippetCompiler.PrepareDetourArtifactAsync(
                target,
                replacementBody,
                references.PreserveSnippets,
                references.SessionInstanceId,
                ct
            );
            if (preparedArtifact.Failure is { } artifactFailure)
                return PreparedDetour.Failed(artifactFailure);
            artifact = preparedArtifact.Artifact!.Value;
        }

        SnippetParseResult parsed;
        try
        {
            parsed = ConduitCodeParser.Parse(artifact.Source);
        }
        catch (SnippetParseException exception)
        {
            return PreparedDetour.Failed(
                new()
                {
                    Outcome = ToolOutcome.CompileError,
                    DisplayName = artifact.FileName,
                    Exception = ToolExceptionInfo.FromException(exception),
                    Diagnostic = exception.Message,
                }
            );
        }

        MetadataReference[] compilationReferences;
        try
        {
            compilationReferences = sessionCache.GetCompilationReferences(method.AssemblyPath);
        }
        catch (Exception exception) when (exception is IOException or BadImageFormatException)
        {
            return PreparedDetour.Failed(
                ToolExecutionResult.FromException(exception, string.Empty, "A compilation reference could not be publicized for detouring.")
            );
        }

        var typeName = "DetourHost_" + artifact.Id;
        var defaultUsings = SnippetCompiler.GetDefaultUsingDirectives(target);
        var inferredNamespaces = new List<string>();
        bool asyncMode = false;
        var output = CompileWithReferences(compilationReferences);
        if (SnippetCompilationEngine.HasErrors(output.Diagnostics))
        {
            MetadataReference[] fullyPublicizedReferences;
            try
            {
                fullyPublicizedReferences = sessionCache.GetFullyPublicizedReferences();
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException)
            {
                return PreparedDetour.Failed(
                    ToolExecutionResult.FromException(
                        exception,
                        string.Empty,
                        "A compilation reference could not be publicized for detouring."
                    )
                );
            }

            // retrying the former all-public reference set preserves cross-assembly private access.
            if (!ReferenceEquals(fullyPublicizedReferences, compilationReferences))
            {
                output = CompileWithReferences(fullyPublicizedReferences);
                sessionCache.CompleteFullPublicization(
                    fullyPublicizedReferences,
                    !SnippetCompilationEngine.HasErrors(output.Diagnostics)
                );
            }
        }

        var errors = SnippetCompilationEngine.FormatDiagnostics(
            output.Diagnostics,
            DiagnosticSeverity.Error
        );
        if (errors.Length > 0)
            return PreparedDetour.Failed(
                new()
                {
                    Outcome = ToolOutcome.CompileError,
                    DisplayName = artifact.FileName,
                    Diagnostic = errors,
                }
            );

        var warnings = SnippetCompilationEngine.FormatDiagnostics(
            output.Diagnostics,
            DiagnosticSeverity.Warning
        );
        var command = BuildCommand(method, probe ? "test" : "apply");
        command.Target = DetourSourceBuilder.GeneratedNamespace + "." + typeName;
        command.DisplayName = artifact.FileName;
        command.Artifacts =
        [
            await SnippetCompiler.CreateArtifactAsync(
                target,
                artifact.Id + ".dll",
                "application/vnd.microsoft.portable-executable",
                references.PreserveSnippets,
                output.AssemblyBytes!,
                ct
            ),
            await SnippetCompiler.CreateArtifactAsync(
                target,
                artifact.Id + ".pdb",
                "application/vnd.microsoft.portable-pdb",
                references.PreserveSnippets,
                output.PdbBytes!,
                ct
            ),
        ];
        return PreparedDetour.Succeeded(command, warnings.Length == 0 ? null : warnings);

        SnippetCompilationEngine.Output CompileWithReferences(
            MetadataReference[] activeReferences)
        {
            var compilation = Compile(
                method,
                typeName,
                artifact.FileName,
                parsed,
                activeReferences,
                defaultUsings,
                inferredNamespaces,
                asyncMode
            );
            if (SnippetCompilationEngine.HasAnyError(
                    compilation.Diagnostics,
                    "CS4032",
                    "CS4033"
                ))
            {
                asyncMode = true;
                compilation = Compile(
                    method,
                    typeName,
                    artifact.FileName,
                    parsed,
                    activeReferences,
                    defaultUsings,
                    inferredNamespaces,
                    asyncMode
                );
            }

            for (var attempt = 0;
                 attempt < 2 && SnippetCompilationEngine.HasErrors(compilation.Diagnostics);
                 ++attempt)
            {
                var inferred = SnippetNamespaceInference.InferNamespaces(
                    compilation.Diagnostics,
                    references.Paths!,
                    inferredNamespaces,
                    sessionCache.NamespaceCandidates
                );
                if (inferred.Count == 0)
                    break;

                inferredNamespaces.AddRange(inferred);
                compilation = Compile(
                    method,
                    typeName,
                    artifact.FileName,
                    parsed,
                    activeReferences,
                    defaultUsings,
                    inferredNamespaces,
                    asyncMode
                );
            }

            return compilation;
        }
    }

    static BridgeCommand BuildCommand(MethodTarget method, string mode) =>
        new()
        {
            CommandType = BridgeCommandTypes.Detour,
            Args =
            [
                mode,
                method.ModuleVersionId.ToString("N"),
                method.MetadataToken.ToString(CultureInfo.InvariantCulture),
                method.SignatureHash,
                method.CanonicalSelector,
                method.ReplacementDeclaration,
            ],
        };

    static SnippetCompilationEngine.Output Compile(
        MethodTarget method,
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        MetadataReference[] references,
        IReadOnlyList<string> defaultUsings,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async)
        => SnippetCompilationEngine.Emit(
            "ConduitDetour_",
            DetourSourceBuilder.BuildSource(
                method,
                typeName,
                sourceFileName,
                parsed,
                defaultUsings,
                inferredNamespaces,
                async
            ),
            sourceFileName,
            references,
            OptimizationLevel.Release
        );

    DetourSessionCache GetSessionCache(
        string target,
        string sessionInstanceId,
        MetadataReference[] standardReferences,
        string[] referencePaths)
    {
        if (sessionCaches.TryGetValue(target, out var cached)
            && cached.SessionInstanceId == sessionInstanceId)
            return cached;

        return sessionCaches.AddOrUpdate(
            target,
            static (_, state) => new(
                state.SessionInstanceId,
                state.StandardReferences,
                state.ReferencePaths
            ),
            static (_, current, state) => current.SessionInstanceId == state.SessionInstanceId
                ? current
                : new(
                    state.SessionInstanceId,
                    state.StandardReferences,
                    state.ReferencePaths
                ),
            (
                SessionInstanceId: sessionInstanceId,
                StandardReferences: standardReferences,
                ReferencePaths: referencePaths
            )
        );
    }

    static ToolExecutionResult ExceptionResult(string diagnostic) =>
        new() { Outcome = ToolOutcome.Exception, Diagnostic = diagnostic };
}
