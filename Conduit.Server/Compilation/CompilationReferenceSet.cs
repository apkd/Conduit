using Microsoft.CodeAnalysis;

namespace Conduit;

// a session owns its reference set and optional detour state. Only immutable standard
// references are shared; an old set never owns the newer set or another target's detour cache.
sealed class CompilationReferenceSet
{
    readonly Dictionary<ReferenceIdentity, PortableExecutableReference> reusableReferences;
    readonly Lazy<DetourSessionCache> detours;

    CompilationReferenceSet(
        string sessionInstanceId,
        MetadataReference[] references,
        string[] paths,
        Dictionary<ReferenceIdentity, PortableExecutableReference> reusableReferences)
    {
        References = references;
        Paths = paths;
        this.reusableReferences = reusableReferences;
        detours = new(() => new(sessionInstanceId, references, paths));
    }

    internal MetadataReference[] References { get; }
    internal string[] Paths { get; }
    internal DetourSessionCache GetDetourCache() => detours.Value;

    internal static CompilationReferenceSet Create(
        string sessionInstanceId,
        IReadOnlyCollection<BridgeAssemblyReference> manifest,
        IReadOnlyDictionary<string, string> resolvedPaths,
        IEnumerable<CompilationReferenceSet?> existing)
    {
        // this temporary lookup is discarded after construction. There is no process-wide
        // strong cache accumulating every historical module version.
        var available = new Dictionary<ReferenceIdentity, PortableExecutableReference>();
        foreach (var set in existing)
            if (set is not null)
                foreach (var pair in set.reusableReferences)
                    available.TryAdd(pair.Key, pair.Value);

        var references = new List<MetadataReference>(manifest.Count);
        var paths = new List<string>(manifest.Count);
        var reusable = new Dictionary<ReferenceIdentity, PortableExecutableReference>();
        foreach (var item in manifest)
        {
            if (!resolvedPaths.TryGetValue(item.Id, out var path))
                continue;

            try
            {
                var identity = ReferenceIdentity.TryCreate(path, item.Id, item.Length);
                PortableExecutableReference reference;
                if (identity is { } key && available.TryGetValue(key, out var shared))
                {
                    reference = shared;
                    reusable.TryAdd(key, shared);
                }
                else
                {
                    using var stream = File.OpenRead(path);
                    var length = stream.Length;
                    reference = CompilationMetadata.CreateReference(stream, path);

                    // opaque IDs, mismatched on-disk MVIDs, unknown lengths and multi-module
                    // assemblies still compile as before, but are not eligible for sharing.
                    if (identity is { } loadedKey
                        && length == loadedKey.Length
                        && HasModuleIdentity(reference, loadedKey.ModuleVersionId))
                    {
                        reusable.TryAdd(loadedKey, reference);
                        available.TryAdd(loadedKey, reference);
                    }
                }

                references.Add(reference);
                paths.Add(path);
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException)
            {
                // native and transiently unavailable images can appear in the Unity manifest.
            }
        }

        return new(sessionInstanceId, references.ToArray(), paths.ToArray(), reusable);
    }

    static bool HasModuleIdentity(PortableExecutableReference reference, Guid expected)
    {
        var metadata = reference.GetMetadata();
        try
        {
            if (metadata is not AssemblyMetadata assembly)
                return false;
            var modules = assembly.GetModules();
            if (modules.Length != 1)
                return false;
            var reader = modules[0].GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid) == expected;
        }
        finally
        {
            GC.KeepAlive(metadata);
            GC.KeepAlive(reference);
        }
    }

    readonly record struct ReferenceIdentity(string Path, Guid ModuleVersionId, long Length)
    {
        internal static ReferenceIdentity? TryCreate(string path, string id, long length)
        {
            if (length <= 0 || !Guid.TryParse(id, out var moduleVersionId) || moduleVersionId == Guid.Empty)
                return null;

            var canonicalPath = System.IO.Path.GetFullPath(path);
            if (OperatingSystem.IsWindows())
                canonicalPath = canonicalPath.ToUpperInvariant();
            return new(canonicalPath, moduleVersionId, length);
        }
    }
}
