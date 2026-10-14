using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace Conduit;

static partial class SnippetNamespaceInference
{
    const int MaximumCacheEntries = 1024;

    internal static List<string> InferNamespaces(
        IEnumerable<Diagnostic> diagnostics,
        IReadOnlyCollection<MetadataReference> references,
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
            foreach (var reference in references)
            {
                if (reference is not PortableExecutableReference portable)
                    continue;

                try
                {
                    var metadata = portable.GetMetadata();
                    try
                    {
                        if (metadata is AssemblyMetadata assembly)
                            foreach (var assemblyModule in assembly.GetModules())
                                Scan(assemblyModule.GetMetadataReader());
                        else if (metadata is ModuleMetadata standaloneModule)
                            Scan(standaloneModule.GetMetadataReader());
                    }
                    finally
                    {
                        // GetMetadata returns a non-owning copy. Keep the snapshot owner alive while scanning.
                        GC.KeepAlive(metadata);
                        GC.KeepAlive(portable);
                    }
                }
                catch (Exception exception) when (exception is IOException or BadImageFormatException) { }
            }

        if (namespaceCache != null)
        {
            var remainingCacheEntries = MaximumCacheEntries - namespaceCache.Count;
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

        void Scan(MetadataReader reader)
        {
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
    }

    [GeneratedRegex(@"['""](?<symbol>[A-Za-z_][A-Za-z0-9_]*)['""]")]
    internal static partial Regex MissingSymbolRegex();
}
