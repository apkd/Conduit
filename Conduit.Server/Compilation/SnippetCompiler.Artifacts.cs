namespace Conduit;

public sealed partial class SnippetCompiler
{
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
}
