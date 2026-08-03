using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Conduit;

/// <summary>Resolves a loaded Mono method and compiles an ABI-identical replacement.</summary>
public sealed class DetourCompiler(SnippetCompiler snippetCompiler)
{
    const string GeneratedNamespace = "ConduitGenerated.Detour";

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

        var resolution = MethodCatalog.Create(references.Paths!).Resolve(selector);
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
            compilationReferences = references.Paths!
                .Select(path => MetadataReference.CreateFromImage(
                    ImmutableArray.Create(MetadataPublicizer.Publicize(path)),
                    filePath: path
                ))
                .ToArray();
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
        var output = Compile(
            method,
            typeName,
            artifact.FileName,
            parsed,
            compilationReferences,
            defaultUsings,
            inferredNamespaces,
            asyncMode
        );
        if (SnippetCompiler.HasAnyError(output.Diagnostics, "CS4032", "CS4033"))
        {
            asyncMode = true;
            output = Compile(
                method,
                typeName,
                artifact.FileName,
                parsed,
                compilationReferences,
                defaultUsings,
                inferredNamespaces,
                asyncMode
            );
        }

        for (int attempt = 0; attempt < 2 && SnippetCompiler.HasErrors(output.Diagnostics); ++attempt)
        {
            var inferred = SnippetCompiler.InferNamespaces(
                output.Diagnostics,
                references.Paths!,
                inferredNamespaces
            );
            if (inferred.Count == 0)
                break;

            inferredNamespaces.AddRange(inferred);
            output = Compile(
                method,
                typeName,
                artifact.FileName,
                parsed,
                compilationReferences,
                defaultUsings,
                inferredNamespaces,
                asyncMode
            );
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
        var builder = new StringBuilder();
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
        var parameters = new List<string>();
        if (!method.IsStatic)
            parameters.Add((method.DeclaringType.IsValueType ? "ref " : string.Empty) + method.DeclaringType.Source + " @this");
        for (int index = 0; index < method.Parameters.Length; ++index)
            parameters.Add(method.Parameters[index].Declaration("arg" + index));
        builder.Append(string.Join(", ", parameters)).AppendLine(")");
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
