using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Conduit;

/// <summary>Resolves a loaded Mono method and compiles an ABI-identical replacement.</summary>
public sealed class DetourCompiler(SnippetCompiler snippetCompiler)
{
    const string GeneratedNamespace = "ConduitGenerated.Detour";
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
            artifact = new("test", "test.cs", BuildProbeBody(method.ReturnType));
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
        if (SnippetCompiler.HasErrors(output.Diagnostics))
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
                    !SnippetCompiler.HasErrors(output.Diagnostics)
                );
            }
        }

        var errors = SnippetCompiler.FormatDiagnostics(output.Diagnostics, DiagnosticSeverity.Error);
        if (errors.Length > 0)
            return PreparedDetour.Failed(
                new()
                {
                    Outcome = ToolOutcome.CompileError,
                    DisplayName = artifact.FileName,
                    Diagnostic = errors,
                }
            );

        var warnings = SnippetCompiler.FormatDiagnostics(output.Diagnostics, DiagnosticSeverity.Warning);
        var command = BuildCommand(method, probe ? "test" : "apply");
        command.Target = GeneratedNamespace + "." + typeName;
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

        SnippetCompiler.CompilationOutput CompileWithReferences(
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
            if (SnippetCompiler.HasAnyError(compilation.Diagnostics, "CS4032", "CS4033"))
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
                 attempt < 2 && SnippetCompiler.HasErrors(compilation.Diagnostics);
                 ++attempt)
            {
                var inferred = SnippetCompiler.InferNamespaces(
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

    sealed class DetourSessionCache(
        string sessionInstanceId,
        MetadataReference[] standardReferences,
        string[] referencePaths)
    {
        readonly object catalogGate = new();
        readonly object referencesGate = new();
        readonly Dictionary<string, MetadataReference[]> targetedReferences =
            new(StringComparer.OrdinalIgnoreCase);
        MethodCatalog? methodCatalog;
        MetadataReference[]? fullyPublicizedReferences;

        internal string SessionInstanceId { get; } = sessionInstanceId;
        internal ConcurrentDictionary<string, string[]> NamespaceCandidates { get; } = new(StringComparer.Ordinal);

        internal MethodCatalog GetMethodCatalog()
        {
            lock (catalogGate)
                return methodCatalog ??= MethodCatalog.Create(referencePaths);
        }

        internal MetadataReference[] GetCompilationReferences(string targetAssemblyPath)
        {
            lock (referencesGate)
            {
                if (fullyPublicizedReferences != null)
                    return fullyPublicizedReferences;
                if (targetedReferences.TryGetValue(targetAssemblyPath, out var cached))
                    return cached;

                var references = (MetadataReference[])standardReferences.Clone();
                for (var index = 0; index < referencePaths.Length; ++index)
                {
                    if (!string.Equals(
                            referencePaths[index],
                            targetAssemblyPath,
                            StringComparison.OrdinalIgnoreCase
                        ))
                        continue;

                    references[index] = CreatePublicReference(targetAssemblyPath);
                    targetedReferences.Add(targetAssemblyPath, references);
                    return references;
                }

                throw new FileNotFoundException(
                    "The detour target assembly was not present in the compiler reference set.",
                    targetAssemblyPath
                );
            }
        }

        internal MetadataReference[] GetFullyPublicizedReferences()
        {
            lock (referencesGate)
            {
                if (fullyPublicizedReferences != null)
                    return fullyPublicizedReferences;

                var references = new MetadataReference[referencePaths.Length];
                for (var index = 0; index < referencePaths.Length; ++index)
                    if (targetedReferences.TryGetValue(referencePaths[index], out var targeted)
                        && !ReferenceEquals(targeted[index], standardReferences[index]))
                        references[index] = targeted[index];

                if (referencePaths.Length == 1 && references[0] == null)
                    references[0] = CreatePublicReference(referencePaths[0]);
                else if (referencePaths.Length > 1)
                {
                    var errors = new ExceptionDispatchInfo?[referencePaths.Length];
                    Parallel.For(0, referencePaths.Length, index =>
                    {
                        if (references[index] != null)
                            return;

                        try
                        {
                            references[index] = CreatePublicReference(referencePaths[index]);
                        }
                        catch (Exception exception)
                        {
                            errors[index] = ExceptionDispatchInfo.Capture(exception);
                        }
                    });

                    foreach (var error in errors)
                        error?.Throw();
                }

                return fullyPublicizedReferences = references;
            }
        }

        internal void CompleteFullPublicization(MetadataReference[] references, bool succeeded)
        {
            lock (referencesGate)
                if (ReferenceEquals(fullyPublicizedReferences, references))
                {
                    if (succeeded)
                        targetedReferences.Clear();
                    else
                        fullyPublicizedReferences = null; // invalid snippets must not pin every publicized assembly
                }
        }

        static MetadataReference CreatePublicReference(string path)
            => MetadataReference.CreateFromImage(
                ImmutableCollectionsMarshal.AsImmutableArray(
                    MetadataPublicizer.Publicize(path)
                ),
                filePath: path
            );
    }

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

    internal static string BuildProbeBody(CSharpType returnType) =>
        returnType switch
        {
            { IsByRef: true } => "throw new global::System.NotSupportedException();",
            { Source: "void" } => "return;",
            _ => "return default;",
        };

    static SnippetCompiler.CompilationOutput Compile(
        MethodTarget method,
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        MetadataReference[] references,
        IReadOnlyList<string> defaultUsings,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async)
        => SnippetCompiler.Emit(
            "ConduitDetour_",
            BuildSource(
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

    internal static string BuildSource(
        MethodTarget method,
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        IReadOnlyList<string> defaultUsings,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async)
    {
        var builder = new StringBuilder(parsed.Body.Text.Length + 1024);
        SnippetCompiler.AppendUsingDirectives(
            builder,
            defaultUsings,
            inferredNamespaces,
            parsed.Usings,
            sourceFileName
        );

        builder.AppendLine("#pragma warning disable CS0162, CS1998");
        builder.Append("namespace ").AppendLine(GeneratedNamespace);
        builder.AppendLine("{");
        foreach (var declaration in parsed.TypeDeclarations)
            SnippetCompiler.AppendChunk(builder, declaration, sourceFileName);
        builder.Append("public static class ").AppendLine(typeName);
        builder.AppendLine("{");
        foreach (var field in parsed.StaticFields)
            SnippetCompiler.AppendChunk(builder, field, sourceFileName);
        builder.AppendLine("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        builder.Append("public static unsafe ");
        if (async)
            builder.Append("async ");
        builder.Append(method.ReturnType.ReturnDeclaration)
            .Append(" Replace(");
        var hasParameter = false;
        if (!method.IsStatic)
        {
            if (method.DeclaringType.IsValueType)
                builder.Append("ref ");
            builder.Append(method.DeclaringType.Source).Append(" @this");
            hasParameter = true;
        }
        for (int index = 0; index < method.Parameters.Length; ++index)
        {
            if (hasParameter)
                builder.Append(", ");
            builder.Append(method.Parameters[index].Declaration("arg" + index));
            hasParameter = true;
        }
        builder.AppendLine(")");
        builder.AppendLine("{");
        SnippetCompiler.AppendChunk(builder, parsed.Body, sourceFileName);
        builder.AppendLine("}");
        builder.AppendLine("public static int AccessProbe() => global::Conduit.DetourAccessProbe.Value;");
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("#pragma warning restore CS0162, CS1998");
        return builder.ToString();
    }

    static ToolExecutionResult ExceptionResult(string diagnostic) =>
        new() { Outcome = ToolOutcome.Exception, Diagnostic = diagnostic };

}

readonly record struct PreparedDetour(
    BridgeCommand? Command,
    ToolExecutionResult? Failure,
    string? Warning)
{
    internal static PreparedDetour Succeeded(BridgeCommand command, string? warning = null) =>
        new(command, null, warning);

    internal static PreparedDetour Failed(ToolExecutionResult failure) => new(null, failure, null);
}
