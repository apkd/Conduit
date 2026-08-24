using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Conduit;

static class SnippetCompilationEngine
{
    static readonly CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
    static readonly CSharpCompilationOptions debugCompilationOptions = CreateCompilationOptions(OptimizationLevel.Debug);
    static readonly CSharpCompilationOptions releaseCompilationOptions = CreateCompilationOptions(OptimizationLevel.Release);
    static readonly EmitOptions emitOptions = new(debugInformationFormat: DebugInformationFormat.PortablePdb);

    internal static Output Compile(
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        MetadataReference[] references,
        IReadOnlyList<string> defaultUsingDirectives,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async,
        bool returnsValue)
        => Emit(
            "ConduitSnippet_",
            SnippetSourceBuilder.BuildSource(
                typeName,
                sourceFileName,
                parsed,
                defaultUsingDirectives,
                inferredNamespaces,
                async,
                returnsValue
            ),
            sourceFileName,
            references,
            OptimizationLevel.Debug
        );

    internal static Output Emit(
        string assemblyNamePrefix,
        string source,
        string sourceFileName,
        IEnumerable<MetadataReference> references,
        OptimizationLevel optimizationLevel)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            parseOptions,
            sourceFileName,
            Encoding.UTF8
        );
        var compilation = CSharpCompilation.Create(
            assemblyNamePrefix + Guid.NewGuid().ToString("N"),
            [syntaxTree],
            references,
            optimizationLevel == OptimizationLevel.Release
                ? releaseCompilationOptions
                : debugCompilationOptions
        );
        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(
            assembly,
            pdb,
            options: emitOptions
        );
        return new(
            emit.Diagnostics,
            emit.Success ? assembly.ToArray() : null,
            emit.Success ? pdb.ToArray() : null
        );
    }

    static CSharpCompilationOptions CreateCompilationOptions(OptimizationLevel optimizationLevel)
        => new(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: optimizationLevel,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable
        );

    internal static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error);

    internal static bool HasAnyError(IEnumerable<Diagnostic> diagnostics, string id)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == id)
                return true;

        return false;
    }

    internal static bool HasAnyError(
        IEnumerable<Diagnostic> diagnostics,
        string firstId,
        string secondId)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && (diagnostic.Id == firstId || diagnostic.Id == secondId))
                return true;

        return false;
    }

    internal static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics,
        DiagnosticSeverity severity) =>
        string.Join(
            "\n",
            diagnostics
                .Where(value => value.Severity == severity)
                .OrderBy(static value => value.Location.SourceSpan.Start)
                .Select(static value => value.ToString())
        );

    internal readonly record struct Output(
        IEnumerable<Diagnostic> Diagnostics,
        byte[]? AssemblyBytes,
        byte[]? PdbBytes);
}
