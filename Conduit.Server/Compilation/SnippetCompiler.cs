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
    const int MaximumNamespaceCacheEntries = 1024;
    const string SnippetNamespace = "ConduitGenerated.ExecuteCode";
    static readonly TimeSpan referenceCommandTimeout = TimeSpan.FromSeconds(30);
    static readonly CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
    static readonly CSharpCompilationOptions debugCompilationOptions = CreateCompilationOptions(OptimizationLevel.Debug);
    static readonly CSharpCompilationOptions releaseCompilationOptions = CreateCompilationOptions(OptimizationLevel.Release);
    static readonly EmitOptions emitOptions = new(debugInformationFormat: DebugInformationFormat.PortablePdb);
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

    readonly ConcurrentDictionary<string, TargetSessionCache> sessionCaches
        = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, ReferenceFlight> referenceFlights
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
        var references = await GetReferencesAsync(target, ct);
        if (references.Failure is { } referenceFailure)
            return SnippetCompilation.FromFailure(referenceFailure);

        var snippetRoot = GetSnippetRoot(target, references.PreserveSnippets);
        var sessionCache = GetSessionCache(target, references.SessionInstanceId);
        var cache = sessionCache.GetCompilationCache(snippetRoot);
        await cache.Gate.WaitAsync(ct);
        try
        {
            var source = snippet;
            string? requestedFileName = null;
            if (TryParseScriptFileName(snippet, out var named))
            {
                if (cache.ByName.TryGetValue(named, out var namedCompilation))
                    return SnippetCompilation.Success(namedCompilation);

                source = await LoadScriptSourceAsync(cache, snippetRoot, named, ct);
                if (source is null)
                    return SnippetCompilation.FromFailure(
                        CompileError($"Snippet '{snippet}' was not found in the current target session.")
                    );

                requestedFileName = named;
            }

            if (requestedFileName is null && cache.BySource.TryGetValue(source, out var cached))
                return SnippetCompilation.Success(cached);

            var artifactId = GetNextArtifactId(cache, snippetRoot);
            var sourceFileName = requestedFileName ?? artifactId + ".cs";
            var typeName = "SnippetHost_" + artifactId;
            var fullTypeName = $"{SnippetNamespace}.{typeName}";
            if (requestedFileName is null)
            {
                cache.SourcesByName[sourceFileName] = source;
                if (snippetRoot is not null)
                {
                    Directory.CreateDirectory(snippetRoot);
                    await File.WriteAllTextAsync(Path.Combine(snippetRoot, sourceFileName), source, ct);
                }
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
                    inferredNamespaces,
                    sessionCache.NamespaceCandidates
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
                    references.PreserveSnippets,
                    compile.AssemblyBytes!,
                    ct
                ),
                await CreateArtifactAsync(
                    target,
                    artifactId + ".pdb",
                    "application/vnd.microsoft.portable-pdb",
                    references.PreserveSnippets,
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
        return new(
            references.References,
            references.ReferencePaths,
            references.PreserveSnippets,
            references.SessionInstanceId,
            references.Failure
        );
    }

    internal static async Task<BridgeArtifact> CreateArtifactAsync(
        string target,
        string name,
        string mediaType,
        bool preserveSnippets,
        byte[] bytes,
        CancellationToken ct)
    {
        if (PlayerSelector.TryParse(target, out _))
            return BridgeArtifact.FromBytes(name, mediaType, bytes);

        var relativePath = Path.Combine(GetSnippetDirectory(preserveSnippets), name);
        var path = Path.Combine(ProjectPathNormalizer.ToPlatformPath(target), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return BridgeArtifact.FromProjectFile(name, mediaType, relativePath, bytes);
    }

    internal async Task<SourceArtifactResult> PrepareDetourArtifactAsync(
        string target,
        string source,
        bool preserveSnippets,
        string sessionInstanceId,
        CancellationToken ct)
    {
        var snippetRoot = GetSnippetRoot(target, preserveSnippets);
        var cache = GetSessionCache(target, sessionInstanceId)
            .GetCompilationCache(snippetRoot);
        await cache.Gate.WaitAsync(ct);
        try
        {
            if (TryParseScriptFileName(source, out var fileName))
            {
                if (cache.DetoursByName.TryGetValue(fileName, out var cachedArtifact))
                    return SourceArtifactResult.Succeeded(cachedArtifact);

                var loadedSource = await LoadScriptSourceAsync(cache, snippetRoot, fileName, ct);
                if (loadedSource is null)
                    return SourceArtifactResult.Failed(CompileError($"Detour source '{source}' was not found."));

                var artifact = new SourceArtifact(
                    GetNextArtifactId(cache, snippetRoot),
                    fileName,
                    loadedSource
                );
                cache.DetoursByName[fileName] = artifact;
                return SourceArtifactResult.Succeeded(artifact);
            }

            var artifactId = GetNextArtifactId(cache, snippetRoot);
            var sourceFileName = artifactId + ".cs";
            cache.SourcesByName[sourceFileName] = source;
            if (snippetRoot is not null)
            {
                Directory.CreateDirectory(snippetRoot);
                await File.WriteAllTextAsync(Path.Combine(snippetRoot, sourceFileName), source, ct);
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

    TargetSessionCache GetSessionCache(string target, string sessionInstanceId)
    {
        if (sessionCaches.TryGetValue(target, out var cached)
            && cached.SessionInstanceId == sessionInstanceId)
            return cached;

        return sessionCaches.AddOrUpdate(
            target,
            static (_, id) => new(id),
            static (_, current, id) => current.SessionInstanceId == id
                ? current
                : new(id),
            sessionInstanceId
        );
    }

    async Task<ReferenceSetResult> GetReferencesAsync(
        string target,
        CancellationToken ct)
    {
        if (bridgeClient.TryGetLiveHandshake(target, out var liveHandshake)
            && liveHandshake is { SessionInstanceId.Length: > 0 })
        {
            var liveSession = GetSessionCache(target, liveHandshake.SessionInstanceId);
            await liveSession.ReferenceGate.WaitAsync(ct);
            try
            {
                // an active connection already proves which Unity session owns the cached manifest.
                if (liveSession.References is { } cached)
                    return ReferenceSetResult.Succeeded(cached, liveHandshake);
            }
            finally
            {
                liveSession.ReferenceGate.Release();
            }
        }

        var flight = referenceFlights.GetOrAdd(target, static _ => new());
        Task<ReferenceSetResult> task;
        lock (flight)
            task = flight.Task is { IsCompleted: false } active
                ? active
                : flight.Task = GetReferencesCoreAsync(target, CancellationToken.None);

        return await task.WaitAsync(ct);
    }

    async Task<ReferenceSetResult> GetReferencesCoreAsync(
        string target,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // refresh mutable handshake preferences while reusing this session's compiled references.
            var execution = await bridgeClient.ExecuteIdempotentCommandAsync(
                target,
                ConduitUtility.CreateRequestId(),
                new()
                {
                    CommandType = BridgeCommandTypes.CompilationReferences,
                    TrackUsage = false,
                },
                referenceCommandTimeout,
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

            if (execution.Handshake is not { SessionInstanceId.Length: > 0 } handshake)
                return ReferenceSetResult.Failed(
                    CompileError("Unity omitted its session identity from the compilation reference response.")
                );

            var session = GetSessionCache(target, handshake.SessionInstanceId);
            await session.ReferenceGate.WaitAsync(ct);
            try
            {
                if (session.References is { } cached)
                    return ReferenceSetResult.Succeeded(cached, handshake);

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
                            "Unity returned an invalid compilation reference manifest."
                        )
                    );
                }

                if (manifest?.References is not { Length: > 0 } manifestReferences)
                    return ReferenceSetResult.Failed(
                        CompileError("Unity returned no usable compilation references.")
                    );

                var resolvedPaths = new Dictionary<string, string>(
                    manifestReferences.Length,
                    StringComparer.OrdinalIgnoreCase
                );
                var missing = new List<BridgeAssemblyReference>(manifestReferences.Length);
                foreach (var reference in manifestReferences)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = TryResolveAccessiblePath(reference);
                    if (path is null
                        && downloadedReferences.TryGetValue(reference.Id, out var downloadedPath)
                        && HasExpectedLength(downloadedPath, reference.Length))
                        path = downloadedPath;

                    if (path is null)
                        missing.Add(reference);
                    else
                        resolvedPaths[reference.Id] = path;
                }

                if (missing.Count > 0)
                {
                    var fetched = await FetchReferencesAsync(
                        target,
                        missing,
                        session.SessionInstanceId,
                        ct
                    );
                    if (fetched.SessionHandshake is not null)
                        continue;
                    if (fetched.Failure is { } fetchFailure)
                        return ReferenceSetResult.Failed(fetchFailure);

                    foreach (var pair in fetched.Paths!)
                        resolvedPaths[pair.Key] = pair.Value;
                }

                var metadataReferences = new List<MetadataReference>(manifestReferences.Length);
                var referencePaths = new List<string>(manifestReferences.Length);
                foreach (var reference in manifestReferences)
                {
                    if (!resolvedPaths.TryGetValue(reference.Id, out var path))
                        continue;

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

                if (metadataReferences.Count == 0)
                    return ReferenceSetResult.Failed(
                        CompileError("Unity returned no valid managed compilation references.")
                    );

                var references = new CachedReferenceSet(
                    metadataReferences.ToArray(),
                    referencePaths.ToArray()
                );
                session.References = references;
                return ReferenceSetResult.Succeeded(references, handshake);
            }
            finally
            {
                session.ReferenceGate.Release();
            }
        }

        return ReferenceSetResult.Failed(
            new()
            {
                Outcome = ToolOutcome.NotConnected,
                Diagnostic = "Unity reloaded repeatedly while compilation references were being read.",
            }
        );
    }

    async Task<ReferenceFetchBatchResult> FetchReferencesAsync(
        string target,
        IReadOnlyList<BridgeAssemblyReference> references,
        string sessionInstanceId,
        CancellationToken ct)
    {
        var execution = await bridgeClient.ExecuteIdempotentCommandAsync(
            target,
            ConduitUtility.CreateRequestId(),
            new()
            {
                CommandType = BridgeCommandTypes.AssemblyBlob,
                Args = references.Select(static reference => reference.Id).ToArray(),
                TrackUsage = false,
            },
            referenceCommandTimeout,
            ct
        );
        if (execution.Handshake is { } handshake
            && !string.Equals(
                handshake.SessionInstanceId,
                sessionInstanceId,
                StringComparison.Ordinal
            ))
            return ReferenceFetchBatchResult.SessionChanged(handshake);

        if (execution.Result?.Outcome != ToolOutcome.Success)
            return ReferenceFetchBatchResult.Failed(
                execution.Result
                ?? UnityProjectOperations.ToToolExecutionResult(
                    target,
                    BridgeCommandTypes.AssemblyBlob,
                    execution,
                    referenceCommandTimeout
                )
            );
        if (execution.Artifacts.Length != references.Count)
            return ReferenceFetchBatchResult.Failed(
                CompileError(
                    $"Unity returned {execution.Artifacts.Length} assembly artifacts for {references.Count} requested references."
                )
            );

        try
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "conduit",
                "player-references"
            );
            Directory.CreateDirectory(directory);
            var paths = new Dictionary<string, string>(references.Count, StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                var artifact = execution.Artifacts[index];
                if (!string.Equals(artifact.Name, reference.Id + ".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Unity returned assembly artifact '{artifact.Name}' for reference '{reference.Id}'."
                    );

                var bytes = artifact.Decode();
                if (reference.Length > 0 && bytes.LongLength != reference.Length)
                    throw new InvalidDataException(
                        $"Assembly '{reference.AssemblyName}' changed length during transfer."
                    );

                var path = Path.Combine(directory, artifact.Sha256 + ".dll");
                if (!FileMatches(path, bytes))
                    File.WriteAllBytes(path, bytes);

                downloadedReferences[reference.Id] = path;
                paths[reference.Id] = path;
            }

            return ReferenceFetchBatchResult.Succeeded(paths);

            static bool FileMatches(string path, byte[] expected)
            {
                try
                {
                    if (new FileInfo(path) is not { Exists: true } file
                        || file.Length != expected.LongLength)
                        return false;

                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 1,
                        FileOptions.SequentialScan
                    );
                    Span<byte> buffer = stackalloc byte[16 * 1024];
                    var offset = 0;
                    while (offset < expected.Length)
                    {
                        var count = stream.Read(buffer[..Math.Min(buffer.Length, expected.Length - offset)]);
                        if (count == 0
                            || !buffer[..count].SequenceEqual(expected.AsSpan(offset, count)))
                            return false;

                        offset += count;
                    }

                    return stream.ReadByte() < 0;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }
        catch (Exception exception)
        {
            return ReferenceFetchBatchResult.Failed(
                ToolExecutionResult.FromException(
                    exception,
                    string.Empty,
                    "One or more Unity assemblies failed transfer verification."
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

        return HasExpectedLength(path, reference.Length) ? path : null;
    }

    static bool HasExpectedLength(string path, long expectedLength)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && (expectedLength <= 0 || file.Length == expectedLength);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static string GetSnippetDirectory(bool preserveSnippets)
        => Path.Combine(preserveSnippets ? "Library" : "Temp", "Conduit");

    static string? GetSnippetRoot(string target, bool preserveSnippets) =>
        PlayerSelector.TryParse(target, out _)
            ? null
            : Path.Combine(
                ProjectPathNormalizer.ToPlatformPath(target),
                GetSnippetDirectory(preserveSnippets)
            );

    static async Task<string?> LoadScriptSourceAsync(
        TargetCompilationCache cache,
        string? snippetRoot,
        string fileName,
        CancellationToken ct)
    {
        if (cache.SourcesByName.TryGetValue(fileName, out var cached))
            return cached;

        var path = snippetRoot is null ? null : Path.Combine(snippetRoot, fileName);
        if (path is null || !File.Exists(path))
            return null;

        var source = await File.ReadAllTextAsync(path, ct);
        cache.SourcesByName[fileName] = source;
        return source;
    }

    static string GetNextArtifactId(TargetCompilationCache cache, string? snippetRoot)
    {
        // each tool compiles shared sources differently, so their binary outputs need distinct names.
        string artifactId;
        do
            artifactId = (++cache.NextArtifactId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (snippetRoot is not null
               && (File.Exists(Path.Combine(snippetRoot, artifactId + ".cs"))
                   || File.Exists(Path.Combine(snippetRoot, artifactId + ".dll"))
                   || File.Exists(Path.Combine(snippetRoot, artifactId + ".pdb"))));

        return artifactId;
    }

    static int GetHighestArtifactId(string? snippetRoot)
    {
        if (snippetRoot is null || !Directory.Exists(snippetRoot))
            return 0;

        int highest = 0;
        foreach (var path in Directory.EnumerateFiles(snippetRoot))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (int.TryParse(name, out var id)
                && id > 0
                && name == id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                highest = Math.Max(highest, id);
        }

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

    static string BuildSource(
        string typeName,
        string sourceFileName,
        SnippetParseResult parsed,
        IReadOnlyList<string> defaultUsingDirectives,
        IReadOnlyCollection<string> inferredNamespaces,
        bool async,
        bool returnsValue)
    {
        var builder = new StringBuilder(parsed.Body.Text.Length + 1024);
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
        IReadOnlyCollection<string> existing,
        ConcurrentDictionary<string, string[]>? namespaceCache = null)
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
        var uncachedSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (namespaceCache?.TryGetValue(symbol, out var cached) == true)
                candidates[symbol].UnionWith(cached);
            else
                uncachedSymbols.Add(symbol);
        }

        if (uncachedSymbols.Count > 0)
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
                        if (uncachedSymbols.Count <= 6)
                        {
                            foreach (var symbol in uncachedSymbols)
                            {
                                if (!reader.StringComparer.Equals(definition.Name, symbol))
                                    continue;

                                var value = reader.GetString(definition.Namespace);
                                if (value.Length > 0)
                                    candidates[symbol].Add(value);
                            }
                            continue;
                        }

                        // decoding once and hashing scales better than comparing every handle to a large error set.
                        var name = reader.GetString(definition.Name);
                        if (!uncachedSymbols.TryGetValue(name, out var matchedSymbol))
                            continue;

                        var namespaceName = reader.GetString(definition.Namespace);
                        if (namespaceName.Length > 0)
                            candidates[matchedSymbol].Add(namespaceName);
                    }
                }
                catch (Exception exception) when (exception is IOException or BadImageFormatException) { }
            }

        if (namespaceCache != null)
        {
            var remainingCacheEntries = MaximumNamespaceCacheEntries - namespaceCache.Count;
            foreach (var symbol in uncachedSymbols)
            {
                if (remainingCacheEntries-- <= 0)
                    break;
                namespaceCache.TryAdd(symbol, [.. candidates[symbol]]);
            }
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

    internal static bool HasAnyError(IEnumerable<Diagnostic> diagnostics, string id)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == id)
                return true;

        return false;
    }

    internal static bool HasAnyError(IEnumerable<Diagnostic> diagnostics, string firstId, string secondId)
    {
        foreach (var diagnostic in diagnostics)
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && (diagnostic.Id == firstId || diagnostic.Id == secondId))
                return true;

        return false;
    }

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

    static bool TryParseScriptFileName(string value, out string fileName)
    {
        fileName = string.Empty;
        if (value.Length <= 3
            || !value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || value != Path.GetFileName(value))
            return false;

        fileName = value;
        return true;
    }

    [GeneratedRegex(@"['""](?<symbol>[A-Za-z_][A-Za-z0-9_]*)['""]")]
    internal static partial Regex MissingSymbolRegex();

    sealed class TargetSessionCache(string sessionInstanceId)
    {
        readonly ConcurrentDictionary<string, TargetCompilationCache> compilationCaches
            = new(StringComparer.Ordinal);

        internal string SessionInstanceId { get; } = sessionInstanceId;
        internal SemaphoreSlim ReferenceGate { get; } = new(1, 1);
        internal CachedReferenceSet? References { get; set; }
        internal ConcurrentDictionary<string, string[]> NamespaceCandidates { get; } = new(StringComparer.Ordinal);

        internal TargetCompilationCache GetCompilationCache(string? snippetRoot) =>
            compilationCaches.GetOrAdd(
                snippetRoot ?? string.Empty,
                static (_, root) => new(GetHighestArtifactId(root)),
                snippetRoot
            );
    }

    sealed class ReferenceFlight
    {
        internal Task<ReferenceSetResult>? Task { get; set; }
    }

    // execute_code and detour share one sequence within each session storage root.
    sealed class TargetCompilationCache(int nextArtifactId)
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int NextArtifactId { get; set; } = nextArtifactId;
        internal Dictionary<string, CompiledSnippet> BySource { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, CompiledSnippet> ByName { get; } = new(StringComparer.Ordinal);
        // raw sources remain available when editor files cannot be shared with a player target.
        internal Dictionary<string, string> SourcesByName { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, SourceArtifact> DetoursByName { get; } = new(StringComparer.Ordinal);
    }

    internal readonly record struct CompilationOutput(
        IEnumerable<Diagnostic> Diagnostics,
        byte[]? AssemblyBytes,
        byte[]? PdbBytes);

    readonly record struct ReferenceSetResult(
        MetadataReference[]? References,
        string[]? ReferencePaths,
        string SessionInstanceId,
        bool PreserveSnippets,
        ToolExecutionResult? Failure)
    {
        internal static ReferenceSetResult Succeeded(
            CachedReferenceSet references,
            BridgeProjectHandshake handshake) =>
            new(
                references.References,
                references.Paths,
                handshake.SessionInstanceId,
                handshake.PreserveSnippets,
                null
            );

        internal static ReferenceSetResult Failed(ToolExecutionResult failure) =>
            new(null, null, string.Empty, false, failure);
    }

    readonly record struct ReferenceFetchBatchResult(
        Dictionary<string, string>? Paths,
        BridgeProjectHandshake? SessionHandshake,
        ToolExecutionResult? Failure)
    {
        internal static ReferenceFetchBatchResult Succeeded(Dictionary<string, string> paths) =>
            new(paths, null, null);

        internal static ReferenceFetchBatchResult SessionChanged(BridgeProjectHandshake handshake) =>
            new(null, handshake, null);

        internal static ReferenceFetchBatchResult Failed(ToolExecutionResult failure) =>
            new(null, null, failure);
    }

    sealed record CachedReferenceSet(MetadataReference[] References, string[] Paths);
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

readonly record struct CompilationReferencePaths(
    MetadataReference[]? References,
    string[]? Paths,
    bool PreserveSnippets,
    string SessionInstanceId,
    ToolExecutionResult? Failure);

readonly record struct SourceArtifact(string Id, string FileName, string Source);

readonly record struct SourceArtifactResult(SourceArtifact? Artifact, ToolExecutionResult? Failure)
{
    internal static SourceArtifactResult Succeeded(SourceArtifact artifact) => new(artifact, null);
    internal static SourceArtifactResult Failed(ToolExecutionResult failure) => new(null, failure);
}
