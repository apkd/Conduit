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

/// <summary>Compiles execute-code snippets against assemblies loaded by a Unity target.</summary>
public sealed partial class SnippetCompiler(UnityBridgeClient bridgeClient)
{
    const string SnippetNamespace = "ConduitGenerated.ExecuteCode";
    static readonly TimeSpan referenceCommandTimeout = TimeSpan.FromSeconds(30);
    static readonly string[] playerUsingDirectives =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.IO;",
        "using System.Linq;",
        "using System.Threading.Tasks;",
        "using UnityEngine;",
        "using Object = UnityEngine.Object;",
        "using Reflect = Conduit.ConduitReflect;",
        "using static Conduit.Runtime.ConduitRuntimeSearch;",
    ];

    // execute_code and detour share one sequence so their persisted artifacts cannot overwrite each other.
    readonly ConcurrentDictionary<string, TargetCompilationCache> targetCaches
        = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> downloadedReferences
        = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] editorUsingDirectives =
    [
        "using System;",
        "using System.Collections.Generic;",
        "using System.IO;",
        "using System.Linq;",
        "using System.Threading.Tasks;",
        "using UnityEditor;",
        "using UnityEngine;",
        "using Object = UnityEngine.Object;",
        "using Reflect = Conduit.ConduitReflect;",
        "using static Conduit.ConduitSearch;",
    ];

    internal static IReadOnlyList<string> GetDefaultUsingDirectives(string target) =>
        PlayerSelector.TryParse(target, out _) ? playerUsingDirectives : editorUsingDirectives;

    internal async Task<SnippetCompilation> CompileAsync(
        string target,
        string snippet,
        CancellationToken ct)
    {
        var snippetRoot = GetSnippetRoot(target);
        var cache = targetCaches.GetOrAdd(
            target,
            _ => new(GetHighestArtifactId(snippetRoot))
        );
        await cache.Gate.WaitAsync(ct);
        try
        {
            var source = snippet;
            string? requestedArtifactId = null;
            if (TryParseSnippetFileName(snippet, out var named))
            {
                if (cache.ByName.TryGetValue(named, out var namedCompilation))
                    return SnippetCompilation.Success(namedCompilation);

                var sourcePath = snippetRoot is null ? null : Path.Combine(snippetRoot, named);
                var kindPath = snippetRoot is null ? null : Path.Combine(snippetRoot, Path.ChangeExtension(named, ".kind"));
                if (sourcePath is null || !File.Exists(sourcePath))
                    return SnippetCompilation.FromFailure(
                        CompileError($"Snippet '{snippet}' was not found in the current target session.")
                    );

                if (kindPath is null || !File.Exists(kindPath)
                    || File.ReadAllText(kindPath).Trim() != "execute")
                    return SnippetCompilation.FromFailure(
                        CompileError($"Snippet '{snippet}' is not an execute_code artifact.")
                    );

                source = await File.ReadAllTextAsync(sourcePath, ct);
                requestedArtifactId = named[..^3];
            }

            if (requestedArtifactId is null && cache.BySource.TryGetValue(source, out var cached))
                return SnippetCompilation.Success(cached);

            var references = await GetReferencesAsync(target, ct);
            if (references.Failure is { } referenceFailure)
                return SnippetCompilation.FromFailure(referenceFailure);

            var artifactId = requestedArtifactId
                ?? (++cache.NextArtifactId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sourceFileName = artifactId + ".cs";
            var typeName = "SnippetHost_" + artifactId;
            var fullTypeName = $"{SnippetNamespace}.{typeName}";
            if (snippetRoot is not null && requestedArtifactId is null)
            {
                Directory.CreateDirectory(snippetRoot);
                await File.WriteAllTextAsync(Path.Combine(snippetRoot, sourceFileName), source, ct);
                await File.WriteAllTextAsync(Path.Combine(snippetRoot, artifactId + ".kind"), "execute\n", ct);
            }

            SnippetParseResult parsed;
            try
            {
                parsed = ConduitCodeParser.Parse(source);
            }
            catch (SnippetParseException exception)
            {
                return SnippetCompilation.FromFailure(
                    new()
                    {
                        Outcome = ToolOutcome.CompileError,
                        Exception = ToolExceptionInfo.FromException(exception),
                        Diagnostic = exception.Message,
                    }
                );
            }

            var inferredNamespaces = new List<string>();
            var defaultUsingDirectives = GetDefaultUsingDirectives(target);
            bool asyncMode = false;
            bool returnsValue = true;
            var compile = Compile(
                typeName,
                sourceFileName,
                parsed,
                references.References!,
                defaultUsingDirectives,
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
                    defaultUsingDirectives,
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
                    defaultUsingDirectives,
                    inferredNamespaces,
                    async: asyncMode,
                    returnsValue: false
                );
                if (!HasErrors(noResult.Diagnostics))
                {
                    returnsValue = false;
                    compile = noResult;
                }
                else if (TryNormalizeBareReturns(
                             parsed.Body,
                             compile.Diagnostics,
                             noResult.Diagnostics,
                             out var normalizedBody
                         ))
                {
                    parsed = new(
                        parsed.Usings,
                        parsed.TypeDeclarations,
                        parsed.StaticFields,
                        normalizedBody
                    );
                    compile = Compile(
                        typeName,
                        sourceFileName,
                        parsed,
                        references.References!,
                        defaultUsingDirectives,
                        inferredNamespaces,
                        async: asyncMode,
                        returnsValue: true
                    );
                }
            }

            for (int attempt = 0; attempt < 2 && HasErrors(compile.Diagnostics); ++attempt)
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
                    defaultUsingDirectives,
                    inferredNamespaces,
                    async: asyncMode,
                    returnsValue: returnsValue
                );
            }

            var errors = FormatDiagnostics(compile.Diagnostics, DiagnosticSeverity.Error);
            if (errors.Length > 0)
            {
                if (inferredNamespaces.Count > 0)
                    errors = $"Retried with inferred namespaces: {string.Join(", ", inferredNamespaces)}.\n{errors}";

                return SnippetCompilation.FromFailure(
                    new()
                    {
                        Outcome = ToolOutcome.CompileError,
                        Diagnostic = errors,
                    }
                );
            }

            var warnings = FormatDiagnostics(compile.Diagnostics, DiagnosticSeverity.Warning);
            var compiled = new CompiledSnippet(
                sourceFileName,
                fullTypeName,
                await CreateArtifactAsync(
                    target,
                    artifactId + ".dll",
                    "application/vnd.microsoft.portable-executable",
                    compile.AssemblyBytes!,
                    ct
                ),
                await CreateArtifactAsync(
                    target,
                    artifactId + ".pdb",
                    "application/vnd.microsoft.portable-pdb",
                    compile.PdbBytes!,
                    ct
                ),
                warnings.Length == 0 ? null : warnings
            );
            cache.BySource[source] = compiled;
            cache.ByName[sourceFileName] = compiled;
            return SnippetCompilation.Success(compiled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SnippetCompilation.FromFailure(
                ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    "The MCP server could not compile the snippet."
                )
            );
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    internal async Task<CompilationReferencePaths> GetReferencePathsAsync(
        string target,
        CancellationToken ct)
    {
        var references = await GetReferencesAsync(target, ct);
        return new(references.ReferencePaths, references.Failure);
    }

    internal static async Task<BridgeArtifact> CreateArtifactAsync(
        string target,
        string name,
        string mediaType,
        byte[] bytes,
        CancellationToken ct)
    {
        if (PlayerSelector.TryParse(target, out _))
            return BridgeArtifact.FromBytes(name, mediaType, bytes);

        var relativePath = Path.Combine("Temp", "execute_code", name);
        var path = Path.Combine(ProjectPathNormalizer.ToPlatformPath(target), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return BridgeArtifact.FromProjectFile(name, mediaType, relativePath, bytes);
    }

    internal async Task<SourceArtifactResult> PrepareDetourArtifactAsync(
        string target,
        string source,
        CancellationToken ct)
    {
        var snippetRoot = GetSnippetRoot(target);
        var cache = targetCaches.GetOrAdd(
            target,
            _ => new(GetHighestArtifactId(snippetRoot))
        );
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (TryParseSnippetFileName(source, out var fileName))
            {
                if (cache.DetoursByName.TryGetValue(fileName, out var cachedArtifact))
                    return SourceArtifactResult.Succeeded(cachedArtifact);

                var sourcePath = snippetRoot is null ? null : Path.Combine(snippetRoot, fileName);
                var kindPath = snippetRoot is null ? null : Path.Combine(snippetRoot, Path.ChangeExtension(fileName, ".kind"));
                if (sourcePath is null || !File.Exists(sourcePath))
                    return SourceArtifactResult.Failed(CompileError($"Detour source '{source}' was not found."));
                if (kindPath is null || !File.Exists(kindPath)
                    || File.ReadAllText(kindPath).Trim() != "detour")
                    return SourceArtifactResult.Failed(CompileError($"Source '{source}' is not a detour artifact."));

                var artifact = new SourceArtifact(
                    fileName[..^3],
                    fileName,
                    await File.ReadAllTextAsync(sourcePath, ct)
                );
                cache.DetoursByName[fileName] = artifact;
                return SourceArtifactResult.Succeeded(artifact);
            }

            var artifactId = (++cache.NextArtifactId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var sourceFileName = artifactId + ".cs";
            if (snippetRoot is not null)
            {
                Directory.CreateDirectory(snippetRoot);
                await File.WriteAllTextAsync(Path.Combine(snippetRoot, sourceFileName), source, ct);
                await File.WriteAllTextAsync(Path.Combine(snippetRoot, artifactId + ".kind"), "detour\n", ct);
            }

            var sourceArtifact = new SourceArtifact(artifactId, sourceFileName, source);
            cache.DetoursByName[sourceFileName] = sourceArtifact;
            return SourceArtifactResult.Succeeded(sourceArtifact);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return SourceArtifactResult.Failed(
                ToolExecutionResult.FromException(exception, string.Empty, "The detour source artifact could not be prepared.")
            );
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    async Task<ReferenceSetResult> GetReferencesAsync(
        string target,
        CancellationToken ct)
    {
        var execution = await bridgeClient.ExecuteCommandAsync(
            target,
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
                    target,
                    BridgeCommandTypes.CompilationReferences,
                    execution,
                    referenceCommandTimeout
                )
            );

        BridgeAssemblyReferenceManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(
                execution.Result.ReturnValue,
                ConduitJsonContext.Default.BridgeAssemblyReferenceManifest
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
                var fetched = await FetchReferenceAsync(target, reference, ct);
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
        string target,
        BridgeAssemblyReference reference,
        CancellationToken ct)
    {
        if (downloadedReferences.TryGetValue(reference.Id, out var cachedPath)
            && File.Exists(cachedPath))
            return ReferenceFetchResult.Succeeded(cachedPath);

        var execution = await bridgeClient.ExecuteCommandAsync(
            target,
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
                    target,
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

    internal static string? TryResolveAccessiblePath(BridgeAssemblyReference reference)
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

    static string? GetSnippetRoot(string target) =>
        PlayerSelector.TryParse(target, out _)
            ? null
            : Path.Combine(ProjectPathNormalizer.ToPlatformPath(target), "Temp", "execute_code");

    static int GetHighestArtifactId(string? snippetRoot)
    {
        if (snippetRoot is null || !Directory.Exists(snippetRoot))
            return 0;

        int highest = 0;
        foreach (var path in Directory.EnumerateFiles(snippetRoot, "*.cs"))
            if (TryParseSnippetFileName(Path.GetFileName(path), out var fileName))
                highest = Math.Max(
                    highest,
                    int.Parse(fileName.AsSpan(0, fileName.Length - 3), System.Globalization.CultureInfo.InvariantCulture)
                );

        return highest;
    }

    internal static CompilationOutput Compile(
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
            BuildSource(
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

    internal static CompilationOutput Emit(
        string assemblyNamePrefix,
        string source,
        string sourceFileName,
        IEnumerable<MetadataReference> references,
        OptimizationLevel optimizationLevel)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            sourceFileName,
            Encoding.UTF8
        );
        var compilation = CSharpCompilation.Create(
            assemblyNamePrefix + Guid.NewGuid().ToString("N"),
            [syntaxTree],
            references,
            new(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: optimizationLevel,
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
        IReadOnlyList<string> defaultUsingDirectives,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async,
        bool returnsValue)
    {
        var builder = new StringBuilder();
        AppendUsingDirectives(
            builder,
            defaultUsingDirectives,
            inferredNamespaces,
            parsed.Usings,
            sourceFileName
        );

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

    internal static void AppendUsingDirectives(
        StringBuilder builder,
        IReadOnlyList<string> defaultUsingDirectives,
        IReadOnlyCollection<string> inferredNamespaces,
        IEnumerable<SnippetChunk> snippetDirectives,
        string sourceFileName)
    {
        var emittedUsings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directive in defaultUsingDirectives)
            if (emittedUsings.Add(directive))
                builder.AppendLine(directive);

        foreach (var inferredNamespace in inferredNamespaces)
        {
            var directive = $"using {inferredNamespace};";
            if (emittedUsings.Add(directive))
                builder.AppendLine(directive);
        }

        foreach (var directive in snippetDirectives)
        {
            var normalized = directive.Text.Trim();
            if (emittedUsings.Add(normalized))
                AppendChunk(builder, directive, sourceFileName);
        }
    }

    // preserve user coordinates even though snippets are emitted inside a generated wrapper.
    internal static void AppendChunk(
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

    internal static List<string> InferNamespaces(
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

    internal static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error);

    internal static bool HasAnyError(IEnumerable<Diagnostic> diagnostics, params string[] ids) =>
        diagnostics.Any(value =>
            value.Severity == DiagnosticSeverity.Error
            && ids.Contains(value.Id, StringComparer.Ordinal)
        );

    internal static bool TryNormalizeBareReturns(
        SnippetChunk body,
        IEnumerable<Diagnostic> objectResultDiagnostics,
        IEnumerable<Diagnostic> noResultDiagnostics,
        out SnippetChunk normalizedBody)
    {
        // comparing object- and void-return diagnostics identifies bare returns owned by the
        // generated entry point without rewriting invalid returns inside nested local functions.
        normalizedBody = body;
        var remainingNoResultErrors = GetErrorLocationCounts(noResultDiagnostics, "CS0126");
        var recoveredLocations = new HashSet<(int Line, int Column)>();
        foreach (var diagnostic in objectResultDiagnostics)
        {
            if (!TryGetErrorLocation(diagnostic, "CS0126", out var location))
                continue;

            if (remainingNoResultErrors.TryGetValue(location, out var remainingCount)
                && remainingCount > 0)
            {
                remainingNoResultErrors[location] = remainingCount - 1;
                continue;
            }

            recoveredLocations.Add(location);
        }

        if (recoveredLocations.Count == 0)
            return false;

        var insertionOffsets = new List<int>(recoveredLocations.Count);
        foreach (var location in recoveredLocations)
        {
            if (!TryGetBareReturnInsertionOffset(body, location.Line, location.Column, out var insertionOffset))
                return false;
            insertionOffsets.Add(insertionOffset);
        }

        insertionOffsets.Sort();
        var builder = new StringBuilder(body.Text);
        for (int index = insertionOffsets.Count - 1; index >= 0; --index)
            builder.Insert(insertionOffsets[index], " null");

        normalizedBody.Text = builder.ToString();
        return true;

        static Dictionary<(int Line, int Column), int> GetErrorLocationCounts(
            IEnumerable<Diagnostic> diagnostics,
            string id)
        {
            var counts = new Dictionary<(int Line, int Column), int>();
            foreach (var diagnostic in diagnostics)
            {
                if (!TryGetErrorLocation(diagnostic, id, out var location))
                    continue;
                counts.TryGetValue(location, out var count);
                counts[location] = count + 1;
            }
            return counts;
        }

        static bool TryGetErrorLocation(
            Diagnostic diagnostic,
            string id,
            out (int Line, int Column) location)
        {
            location = default;
            if (diagnostic.Id != id
                || diagnostic.Severity != DiagnosticSeverity.Error
                || !diagnostic.Location.IsInSource)
                return false;

            var start = diagnostic.Location.GetMappedLineSpan().StartLinePosition;
            location = (start.Line + 1, start.Character + 1);
            return true;
        }
    }

    static bool TryGetBareReturnInsertionOffset(
        SnippetChunk body,
        int targetLine,
        int targetColumn,
        out int insertionOffset)
    {
        insertionOffset = 0;
        if (targetLine < body.StartLine || targetColumn < 1)
            return false;

        int offset = 0;
        int line = body.StartLine;
        int column = 1;
        while (offset < body.Text.Length && (line != targetLine || column != targetColumn))
        {
            if (body.Text[offset++] == '\n')
            {
                line++;
                column = 1;
            }
            else
                column++;
        }

        const string returnKeyword = "return";
        if (line != targetLine
            || column != targetColumn
            || offset + returnKeyword.Length > body.Text.Length
            || !body.Text.AsSpan(offset, returnKeyword.Length).SequenceEqual(returnKeyword)
            || offset > 0 && IsIdentifierPart(body.Text[offset - 1])
            || offset + returnKeyword.Length < body.Text.Length
            && IsIdentifierPart(body.Text[offset + returnKeyword.Length]))
            return false;

        int cursor = offset + returnKeyword.Length;
        while (cursor < body.Text.Length)
        {
            if (char.IsWhiteSpace(body.Text[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < body.Text.Length
                && body.Text[cursor] == '/'
                && body.Text[cursor + 1] == '/')
            {
                cursor += 2;
                while (cursor < body.Text.Length && body.Text[cursor] != '\n')
                    cursor++;
                continue;
            }

            if (cursor + 1 < body.Text.Length
                && body.Text[cursor] == '/'
                && body.Text[cursor + 1] == '*')
            {
                var commentEnd = body.Text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor = commentEnd + 2;
                continue;
            }

            break;
        }

        if (cursor >= body.Text.Length || body.Text[cursor] != ';')
            return false;

        insertionOffset = offset + returnKeyword.Length;
        return true;
    }

    static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';

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

    sealed class TargetCompilationCache(int nextArtifactId)
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int NextArtifactId { get; set; } = nextArtifactId;
        internal Dictionary<string, CompiledSnippet> BySource { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, CompiledSnippet> ByName { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, SourceArtifact> DetoursByName { get; } = new(StringComparer.Ordinal);
    }

    internal readonly record struct CompilationOutput(
        IEnumerable<Diagnostic> Diagnostics,
        byte[]? AssemblyBytes,
        byte[]? PdbBytes);

    readonly record struct ReferenceSetResult(
        MetadataReference[]? References,
        string[]? ReferencePaths,
        ToolExecutionResult? Failure)
    {
        internal static ReferenceSetResult Succeeded(
            MetadataReference[] references,
            string[] paths) =>
            new(references, paths, null);

        internal static ReferenceSetResult Failed(ToolExecutionResult failure) =>
            new(null, null, failure);
    }

    readonly record struct ReferenceFetchResult(
        string? Path,
        ToolExecutionResult? Failure)
    {
        internal static ReferenceFetchResult Succeeded(string path) => new(path, null);
        internal static ReferenceFetchResult Failed(ToolExecutionResult failure) => new(null, failure);
    }
}

readonly record struct SnippetCompilation(
    CompiledSnippet? Compilation,
    ToolExecutionResult? Failure)
{
    internal static SnippetCompilation Success(CompiledSnippet compilation) =>
        new(compilation, null);

    internal static SnippetCompilation FromFailure(ToolExecutionResult failure) =>
        new(null, failure);
}

sealed record CompiledSnippet(
    string SourceFileName,
    string FullTypeName,
    BridgeArtifact Assembly,
    BridgeArtifact Pdb,
    string? Warning)
{
    internal BridgeCommand ToCommand() =>
        new()
        {
            CommandType = BridgeCommandTypes.ExecuteCode,
            Target = FullTypeName,
            DisplayName = SourceFileName,
            Artifacts = [Assembly, Pdb],
        };
}

readonly record struct CompilationReferencePaths(string[]? Paths, ToolExecutionResult? Failure);

readonly record struct SourceArtifact(string Id, string FileName, string Source);

readonly record struct SourceArtifactResult(SourceArtifact? Artifact, ToolExecutionResult? Failure)
{
    internal static SourceArtifactResult Succeeded(SourceArtifact artifact) => new(artifact, null);
    internal static SourceArtifactResult Failed(ToolExecutionResult failure) => new(null, failure);
}
