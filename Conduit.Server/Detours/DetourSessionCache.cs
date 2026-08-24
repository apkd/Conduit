using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace Conduit;

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

    static PortableExecutableReference CreatePublicReference(string path)
        => MetadataReference.CreateFromImage(
            ImmutableCollectionsMarshal.AsImmutableArray(
                MetadataPublicizer.Publicize(path)
            ),
            filePath: path
        );
}
