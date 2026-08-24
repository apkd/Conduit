using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Conduit;

/// <summary>Compiles execute-code snippets against assemblies loaded by a Unity target.</summary>
public sealed partial class SnippetCompiler(UnityBridgeClient bridgeClient)
{
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
            var fullTypeName = $"{SnippetSourceBuilder.Namespace}.{typeName}";
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
            var compile = SnippetCompilationEngine.Compile(
                typeName,
                sourceFileName,
                parsed,
                references.References!,
                defaultUsingDirectives,
                inferredNamespaces,
                async: asyncMode,
                returnsValue: returnsValue
            );

            if (SnippetCompilationEngine.HasAnyError(compile.Diagnostics, "CS4032", "CS4033"))
            {
                asyncMode = true;
                compile = SnippetCompilationEngine.Compile(
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

            if (SnippetCompilationEngine.HasAnyError(compile.Diagnostics, "CS0126"))
            {
                var noResult = SnippetCompilationEngine.Compile(
                    typeName,
                    sourceFileName,
                    parsed,
                    references.References!,
                    defaultUsingDirectives,
                    inferredNamespaces,
                    async: asyncMode,
                    returnsValue: false
                );
                if (!SnippetCompilationEngine.HasErrors(noResult.Diagnostics))
                {
                    returnsValue = false;
                    compile = noResult;
                }
                else if (SnippetReturnNormalizer.TryNormalizeBareReturns(
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
                    compile = SnippetCompilationEngine.Compile(
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

            for (int attempt = 0;
                 attempt < 2 && SnippetCompilationEngine.HasErrors(compile.Diagnostics);
                 ++attempt)
            {
                var inferred = SnippetNamespaceInference.InferNamespaces(
                    compile.Diagnostics,
                    references.ReferencePaths!,
                    inferredNamespaces,
                    sessionCache.NamespaceCandidates
                );
                if (inferred.Count == 0)
                    break;

                inferredNamespaces.AddRange(inferred);
                compile = SnippetCompilationEngine.Compile(
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

            var errors = SnippetCompilationEngine.FormatDiagnostics(
                compile.Diagnostics,
                DiagnosticSeverity.Error
            );
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

            var warnings = SnippetCompilationEngine.FormatDiagnostics(
                compile.Diagnostics,
                DiagnosticSeverity.Warning
            );
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

    static ToolExecutionResult CompileError(string diagnostic) =>
        new()
        {
            Outcome = ToolOutcome.CompileError,
            Diagnostic = diagnostic,
        };

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

    readonly record struct ReferenceSetResult(
        MetadataReference[]? References,
        string[]? ReferencePaths,
        string SessionInstanceId,
        bool PreserveSnippets,
        ToolExecutionResult? Failure)
    {
        internal static ReferenceSetResult Succeeded(
            CachedReferenceSet references,
            BridgeProjectHandshake handshake,
            bool preserveSnippets) =>
            new(
                references.References,
                references.Paths,
                handshake.SessionInstanceId,
                preserveSnippets,
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
