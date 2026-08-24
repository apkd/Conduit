using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Conduit;

public sealed partial class SnippetCompiler
{
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

    async Task<ReferenceSetResult> GetReferencesAsync(
        string target,
        CancellationToken ct)
    {
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
            // refresh mutable compilation preferences while reusing this session's compiled references.
            var execution = await bridgeClient.ExecuteIdempotentCommandAsync(
                target,
                BridgeIdentifiers.CreateRequestId(),
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

                if (session.References is { } cached)
                    return ReferenceSetResult.Succeeded(
                        cached,
                        handshake,
                        manifest.PreserveSnippets
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
                return ReferenceSetResult.Succeeded(
                    references,
                    handshake,
                    manifest.PreserveSnippets
                );
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
        List<BridgeAssemblyReference> references,
        string sessionInstanceId,
        CancellationToken ct)
    {
        var execution = await bridgeClient.ExecuteIdempotentCommandAsync(
            target,
            BridgeIdentifiers.CreateRequestId(),
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

                var bytes = artifact.ReadVerified();
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
}
