using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Conduit;

public sealed partial class PlayerSnippetCompiler(UnityBridgeClient bridgeClient)
{
    const string SnippetNamespace = "ConduitGenerated.ExecuteCode";
    static readonly TimeSpan referenceCommandTimeout = TimeSpan.FromSeconds(30);
    static readonly string[] defaultUsingDirectives =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.IO;",
        "using System.Linq;",
        "using System.Threading.Tasks;",
        "using UnityEngine;",
        "using Object = UnityEngine.Object;",
        "using Reflect = Conduit.Runtime.ConduitRuntimeReflect;",
        "using static Conduit.Runtime.ConduitRuntimeSearch;",
    ];

    readonly ConcurrentDictionary<string, TargetCompilationCache> targetCaches
        = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> downloadedReferences
        = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<PlayerSnippetCompilation> CompileAsync(
        string playerTarget,
        string snippet,
        CancellationToken ct)
    {
        var cache = targetCaches.GetOrAdd(playerTarget, static _ => new());
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (TryParseSnippetFileName(snippet, out var named)
                && cache.ByName.TryGetValue(named, out var namedCompilation))
                return PlayerSnippetCompilation.Success(namedCompilation);

            if (TryParseSnippetFileName(snippet, out _))
                return PlayerSnippetCompilation.FromFailure(
                    CompileError($"Snippet '{snippet}' was not found in the current player session.")
                );

            if (cache.BySource.TryGetValue(snippet, out var cached))
                return PlayerSnippetCompilation.Success(cached);

            var references = await GetReferencesAsync(playerTarget, ct);
            if (references.Failure is { } referenceFailure)
                return PlayerSnippetCompilation.FromFailure(referenceFailure);

            var artifactId = (++cache.NextArtifactId).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
            var sourceFileName = artifactId + ".cs";
            var typeName = "SnippetHost_" + artifactId;
            var fullTypeName = $"{SnippetNamespace}.{typeName}";
            SnippetParseResult parsed;
            try
            {
                parsed = ConduitCodeParser.Parse(snippet);
            }
            catch (SnippetParseException exception)
            {
                return PlayerSnippetCompilation.FromFailure(
                    new()
                    {
                        Outcome = ToolOutcome.CompileError,
                        Exception = ToolExceptionInfo.FromException(exception),
                        Diagnostic = exception.Message,
                    }
                );
            }

            var inferredNamespaces = new List<string>();
            var asyncMode = false;
            var returnsValue = true;
            var compile = Compile(
                typeName,
                sourceFileName,
                parsed,
                references.References!,
                inferredNamespaces,
                async: asyncMode,
                returnsValue: returnsValue
            );

            if (HasAnyError(compile.Diagnostics, "CS4032", "CS4033"))
            {
                asyncMode = true;
                compile = Compile(
                    typeName,
                    sourceFileName,
                    parsed,
                    references.References!,
                    inferredNamespaces,
                    async: asyncMode,
                    returnsValue: returnsValue
                );
            }

            if (HasAnyError(compile.Diagnostics, "CS0126"))
            {
                var noResult = Compile(
                    typeName,
                    sourceFileName,
                    parsed,
                    references.References!,
                    inferredNamespaces,
                    async: asyncMode,
                    returnsValue: false
                );
                if (!HasErrors(noResult.Diagnostics))
                {
                    returnsValue = false;
                    compile = noResult;
                }
            }

            for (var attempt = 0; attempt < 2 && HasErrors(compile.Diagnostics); attempt++)
            {
                var inferred = InferNamespaces(
                    compile.Diagnostics,
                    references.ReferencePaths!,
                    inferredNamespaces
                );
                if (inferred.Count == 0)
                    break;

                inferredNamespaces.AddRange(inferred);
                compile = Compile(
                    typeName,
                    sourceFileName,
                    parsed,
                    references.References!,
                    inferredNamespaces,
                    async: asyncMode,
                    returnsValue: returnsValue
                );
            }

            var errors = FormatDiagnostics(compile.Diagnostics, DiagnosticSeverity.Error);
            if (errors.Length > 0)
                return PlayerSnippetCompilation.FromFailure(
                    new()
                    {
                        Outcome = ToolOutcome.CompileError,
                        DisplayName = sourceFileName,
                        Diagnostic = errors,
                    }
                );

            var warnings = FormatDiagnostics(compile.Diagnostics, DiagnosticSeverity.Warning);
            var compiled = new CompiledPlayerSnippet(
                sourceFileName,
                fullTypeName,
                BridgeArtifact.FromBytes(
                    artifactId + ".dll",
                    "application/vnd.microsoft.portable-executable",
                    compile.AssemblyBytes!
                ),
                BridgeArtifact.FromBytes(
                    artifactId + ".pdb",
                    "application/vnd.microsoft.portable-pdb",
                    compile.PdbBytes!
                ),
                warnings.Length == 0 ? null : warnings
            );
            cache.BySource[snippet] = compiled;
            cache.ByName[sourceFileName] = compiled;
            return PlayerSnippetCompilation.Success(compiled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PlayerSnippetCompilation.FromFailure(
                ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    "The MCP server could not compile the player snippet."
                )
            );
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    async Task<ReferenceSetResult> GetReferencesAsync(
        string playerTarget,
        CancellationToken ct)
    {
        var execution = await bridgeClient.ExecuteCommandAsync(
            playerTarget,
            ConduitUtility.CreateRequestId(),
            new()
            {
                CommandType = BridgeCommandTypes.CompilationReferences,
                TrackUsage = false,
            },
            referenceCommandTimeout,
            processIdHint: null,
            ct
        );
        if (execution.Result?.Outcome != ToolOutcome.Success
            || string.IsNullOrWhiteSpace(execution.Result.ReturnValue))
            return ReferenceSetResult.Failed(
                execution.Result
                ?? UnityProjectOperations.ToToolExecutionResult(
                    playerTarget,
                    BridgeCommandTypes.CompilationReferences,
                    execution,
                    referenceCommandTimeout
                )
            );

        RuntimeAssemblyReferenceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                execution.Result.ReturnValue,
                ConduitJsonContext.Default.RuntimeAssemblyReferenceManifest
            );
        }
        catch (JsonException exception)
        {
            return ReferenceSetResult.Failed(
                ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    "The player returned an invalid compilation reference manifest."
                )
            );
        }

        if (manifest?.References is not { Length: > 0 })
            return ReferenceSetResult.Failed(
                CompileError("The player returned no usable compilation references.")
            );

        var metadataReferences = new List<MetadataReference>();
        var referencePaths = new List<string>();
        foreach (var reference in manifest.References)
        {
            ct.ThrowIfCancellationRequested();
            var path = TryResolveAccessiblePath(reference);
            if (path is null)
            {
                var fetched = await FetchReferenceAsync(playerTarget, reference, ct);
                if (fetched.Failure is { } failure)
                    return ReferenceSetResult.Failed(failure);
                path = fetched.Path!;
            }

            try
            {
                metadataReferences.Add(MetadataReference.CreateFromFile(path));
                referencePaths.Add(path);
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException)
            {
                // native and facade-only files can appear in a Mono AppDomain location list.
            }
        }

        return metadataReferences.Count == 0
            ? ReferenceSetResult.Failed(
                CompileError("The player returned no valid managed compilation references.")
            )
            : ReferenceSetResult.Succeeded(
                metadataReferences.ToArray(),
                referencePaths.ToArray()
            );
    }

    async Task<ReferenceFetchResult> FetchReferenceAsync(
        string playerTarget,
        RuntimeAssemblyReference reference,
        CancellationToken ct)
    {
        if (downloadedReferences.TryGetValue(reference.Id, out var cachedPath)
            && File.Exists(cachedPath))
            return ReferenceFetchResult.Succeeded(cachedPath);

        var execution = await bridgeClient.ExecuteCommandAsync(
            playerTarget,
            ConduitUtility.CreateRequestId(),
            new()
            {
                CommandType = BridgeCommandTypes.AssemblyBlob,
                Target = reference.Id,
                TrackUsage = false,
            },
            referenceCommandTimeout,
            processIdHint: null,
            ct
        );
        if (execution.Result?.Outcome != ToolOutcome.Success
            || execution.Artifacts is not [var artifact, ..])
            return ReferenceFetchResult.Failed(
                execution.Result
                ?? UnityProjectOperations.ToToolExecutionResult(
                    playerTarget,
                    BridgeCommandTypes.AssemblyBlob,
                    execution,
                    referenceCommandTimeout
                )
            );

        try
        {
            var bytes = artifact.Decode();
            var directory = Path.Combine(
                Path.GetTempPath(),
                "conduit",
                "player-references"
            );
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, artifact.Sha256 + ".dll");
            if (!File.Exists(path))
                File.WriteAllBytes(path, bytes);

            downloadedReferences[reference.Id] = path;
            return ReferenceFetchResult.Succeeded(path);
        }
        catch (Exception exception)
        {
            return ReferenceFetchResult.Failed(
                ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    $"The assembly '{reference.AssemblyName}' failed transfer verification."
                )
            );
        }
    }

    internal static string? TryResolveAccessiblePath(RuntimeAssemblyReference reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Path))
            return null;

        var path = reference.Path;
        if (!OperatingSystem.IsWindows())
        {
            path = path.Replace('\\', '/');
            if (path.StartsWith("Z:/", StringComparison.OrdinalIgnoreCase))
                path = '/' + path[3..];
            else if (path.Length >= 2 && path[1] == ':')
                return null;
        }

        if (!File.Exists(path))
            return null;

        try
        {
            var file = new FileInfo(path);
            return reference.Length <= 0 || file.Length == reference.Length
                ? path
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static CompilationOutput Compile(
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        MetadataReference[] references,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async,
        bool returnsValue)
    {
        var source = BuildSource(
            typeName,
            sourceFileName,
            parsed,
            inferredNamespaces,
            async,
            returnsValue
        );
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            sourceFileName,
            Encoding.UTF8
        );
        var compilation = CSharpCompilation.Create(
            "ConduitSnippet_" + Guid.NewGuid().ToString("N"),
            [syntaxTree],
            references,
            new(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable
            )
        );
        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(
            assembly,
            pdb,
            options: new(debugInformationFormat: DebugInformationFormat.PortablePdb)
        );
        return new(
            emit.Diagnostics,
            emit.Success ? assembly.ToArray() : null,
            emit.Success ? pdb.ToArray() : null
        );
    }

    static string BuildSource(
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async,
        bool returnsValue)
    {
        var builder = new StringBuilder();
        var emittedUsings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directive in defaultUsingDirectives)
        {
            emittedUsings.Add(directive);
            builder.AppendLine(directive);
        }

        foreach (var inferredNamespace in inferredNamespaces)
        {
            var directive = $"using {inferredNamespace};";
            if (emittedUsings.Add(directive))
                builder.AppendLine(directive);
        }

        foreach (var directive in parsed.Usings)
        {
            var normalized = directive.Text.Trim();
            if (emittedUsings.Add(normalized))
                AppendChunk(builder, directive, sourceFileName);
        }

        builder.AppendLine("#pragma warning disable CS0162, CS1998");
        builder.Append("namespace ").AppendLine(SnippetNamespace);
        builder.AppendLine("{");
        foreach (var declaration in parsed.TypeDeclarations)
            AppendChunk(builder, declaration, sourceFileName);

        builder.Append("public static class ").AppendLine(typeName);
        builder.AppendLine("{");
        foreach (var field in parsed.StaticFields)
            AppendChunk(builder, field, sourceFileName);

        builder.Append("public static ");
        if (async)
            builder.Append("async Task");
        else
            builder.Append(returnsValue ? "object" : "void");
        if (async && returnsValue)
            builder.Append("<object>");
        builder.AppendLine(" Execute()");
        builder.AppendLine("{");
        AppendChunk(builder, parsed.Body, sourceFileName);
        if (returnsValue)
        {
            builder.AppendLine("#line hidden");
            builder.AppendLine("return null;");
        }
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("}");
        builder.AppendLine("#pragma warning restore CS0162, CS1998");
        return builder.ToString();
    }

    static void AppendChunk(
        StringBuilder builder,
        SnippetChunk chunk,
        string sourceFileName)
    {
        if (chunk.Text.Length == 0)
            return;

        builder.Append("#line ")
            .Append(Math.Max(1, chunk.StartLine))
            .Append(" \"")
            .Append(sourceFileName.Replace("\\", "\\\\"))
            .AppendLine("\"");
        builder.Append(chunk.Text);
        if (chunk.Text[^1] != '\n')
            builder.AppendLine();
        builder.AppendLine("#line default");
    }

    static List<string> InferNamespaces(
        IEnumerable<Diagnostic> diagnostics,
        IReadOnlyCollection<string> referencePaths,
        IReadOnlyCollection<string> existing)
    {
        var symbols = diagnostics
            .Where(static value => value.Id is "CS0103" or "CS0246")
            .Select(value => MissingSymbolRegex().Match(value.GetMessage()))
            .Where(static value => value.Success)
            .Select(static value => value.Groups["symbol"].Value)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (symbols.Length == 0)
            return [];

        var candidates = symbols.ToDictionary(
            static value => value,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
        foreach (var path in referencePaths)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                    continue;

                var reader = peReader.GetMetadataReader();
                foreach (var handle in reader.TypeDefinitions)
                {
                    var definition = reader.GetTypeDefinition(handle);
                    var name = reader.GetString(definition.Name);
                    if (!candidates.TryGetValue(name, out var namespaces))
                        continue;

                    var value = reader.GetString(definition.Namespace);
                    if (value.Length > 0)
                        namespaces.Add(value);
                }
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException) { }
        }

        return candidates.Values
            .Where(static values => values.Count == 1)
            .Select(static values => values.Single())
            .Where(value => !existing.Contains(value, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error);

    static bool HasAnyError(IEnumerable<Diagnostic> diagnostics, params string[] ids) =>
        diagnostics.Any(value =>
            value.Severity == DiagnosticSeverity.Error
            && ids.Contains(value.Id, StringComparer.Ordinal)
        );

    static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics,
        DiagnosticSeverity severity) =>
        string.Join(
            "\n",
            diagnostics
                .Where(value => value.Severity == severity)
                .OrderBy(static value => value.Location.SourceSpan.Start)
                .Select(static value => value.ToString())
        );

    static ToolExecutionResult CompileError(string diagnostic) =>
        new()
        {
            Outcome = ToolOutcome.CompileError,
            Diagnostic = diagnostic,
        };

    static bool TryParseSnippetFileName(string value, out string fileName)
    {
        fileName = string.Empty;
        if (!value.EndsWith(".cs", StringComparison.Ordinal)
            || !int.TryParse(value.AsSpan(0, value.Length - 3), out var id)
            || id <= 0
            || value != id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".cs")
            return false;

        fileName = value;
        return true;
    }

    [GeneratedRegex(@"['""](?<symbol>[A-Za-z_][A-Za-z0-9_]*)['""]")]
    internal static partial Regex MissingSymbolRegex();

    sealed class TargetCompilationCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int NextArtifactId { get; set; }
        public Dictionary<string, CompiledPlayerSnippet> BySource { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CompiledPlayerSnippet> ByName { get; } = new(StringComparer.Ordinal);
    }

    readonly record struct CompilationOutput(
        IEnumerable<Diagnostic> Diagnostics,
        byte[]? AssemblyBytes,
        byte[]? PdbBytes);

    readonly record struct ReferenceSetResult(
        MetadataReference[]? References,
        string[]? ReferencePaths,
        ToolExecutionResult? Failure)
    {
        public static ReferenceSetResult Succeeded(
            MetadataReference[] references,
            string[] paths) =>
            new(references, paths, null);

        public static ReferenceSetResult Failed(ToolExecutionResult failure) =>
            new(null, null, failure);
    }

    readonly record struct ReferenceFetchResult(
        string? Path,
        ToolExecutionResult? Failure)
    {
        public static ReferenceFetchResult Succeeded(string path) => new(path, null);
        public static ReferenceFetchResult Failed(ToolExecutionResult failure) => new(null, failure);
    }
}

readonly record struct PlayerSnippetCompilation(
    CompiledPlayerSnippet? Compilation,
    ToolExecutionResult? Failure)
{
    public static PlayerSnippetCompilation Success(CompiledPlayerSnippet compilation) =>
        new(compilation, null);

    public static PlayerSnippetCompilation FromFailure(ToolExecutionResult failure) =>
        new(null, failure);
}

sealed record CompiledPlayerSnippet(
    string SourceFileName,
    string FullTypeName,
    BridgeArtifact Assembly,
    BridgeArtifact Pdb,
    string? Warning)
{
    public BridgeCommand ToCommand() =>
        new()
        {
            CommandType = BridgeCommandTypes.ExecuteCode,
            Target = FullTypeName,
            DisplayName = SourceFileName,
            Artifacts = [Assembly, Pdb],
        };
}

sealed class RuntimeAssemblyReferenceManifest
{
    public RuntimeAssemblyReference[] References { get; set; } = [];
}

sealed class RuntimeAssemblyReference
{
    public string Id { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
}
