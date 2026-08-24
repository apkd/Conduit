#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class AssetReferencesTool
    {
        static readonly object cacheGate = new();
        static Dictionary<string, string[]>? reverseReferenceCache;
        static DateTime cacheTimestampUtc = DateTime.MinValue;

        static string CachePath => ConduitPaths.GetReferenceCachePath();

        internal static string GetDependencies(string asset)
            => ConduitAssetPathUtility.ExpandAssetPaths(asset) switch
            {
                { Length: <= 0 }           => $"No assets matched '{asset}'.",
                { Length: 1 } assetPaths   => GetDependenciesForAssetPath(assetPaths[0]),
                { Length: > 1 } assetPaths => BuildAmbiguousSelectionMessage(asset, assetPaths, "get_dependencies"),
            };

        internal static string FindReferencesTo(string asset, bool rebuildCache)
            => ConduitAssetPathUtility.ExpandAssetPaths(asset) switch
            {
                { Length: <= 0 }           => $"No assets matched '{asset}'.",
                { Length: 1 } assetPaths   => FindReferencesToForAssetPath(assetPaths[0], rebuildCache),
                { Length: > 1 } assetPaths => BuildAmbiguousSelectionMessage(asset, assetPaths, "find_references_to"),
            };

        static string GetDependenciesForAssetPath(string assetPath)
        {
            using var pooledDependencies = ConduitPool.GetPooledList<(string Guid, string Path)>(
                out var dependencies
            );

            foreach (var dependencyPath in AssetDatabase.GetDependencies(assetPath, false))
                if (dependencyPath != assetPath)
                    if (AssetDatabase.AssetPathToGUID(dependencyPath) is { Length: > 0 } dependencyGuid)
                        dependencies.Add((dependencyGuid, dependencyPath));

            if (dependencies.Count == 0)
                return $"No direct dependencies found for '{assetPath}'.";

            dependencies.Sort(static (left, right) =>
            {
                var guid = StringComparer.OrdinalIgnoreCase.Compare(left.Guid, right.Guid);
                return guid != 0
                    ? guid
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
            });
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            foreach (var dependency in dependencies)
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(dependency.Guid).Append(" | ").Append(dependency.Path);
            }
            return builder.ToString();
        }

        static string FindReferencesToForAssetPath(string assetPath, bool rebuildCache)
        {
            if (AssetDatabase.AssetPathToGUID(assetPath) is not { Length: > 0 } assetGuid)
                throw new InvalidOperationException($"Could not resolve a GUID for asset '{assetPath}'.");

            var cache = GetOrCreateReverseReferenceCache(rebuildCache);
            if (!cache.TryGetValue(assetGuid, out var referencerGuids) || referencerGuids.Length == 0)
                return $"No direct references found to '{assetPath}'.";

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            foreach (var guid in referencerGuids)
                if (AssetDatabase.GUIDToAssetPath(guid) is { Length: > 0 } path)
                {
                    if (builder.Length > 0)
                        builder.Append('\n');
                    builder.Append(guid).Append(" | ").Append(path);
                }

            if (builder.Length == 0)
                return $"No direct references found to '{assetPath}'.";

            return builder.ToString();
        }

        static string BuildAmbiguousSelectionMessage(string asset, string[] assetPaths, string commandName)
            => $"Asset selector '{asset}' matched {assetPaths.Length} assets, but {commandName} requires a single asset.";

        static Dictionary<string, string[]> GetOrCreateReverseReferenceCache(bool rebuildCache)
        {
            lock (cacheGate)
            {
                if (!rebuildCache)
                    if (reverseReferenceCache != null)
                        if (!IsExpired(cacheTimestampUtc))
                            return reverseReferenceCache;

                if (!rebuildCache && TryLoadCache(out var loadedCache, out var loadedTimestampUtc))
                {
                    var cache = loadedCache
                                ?? throw new InvalidOperationException("Reverse reference cache load returned no cache data.");
                    reverseReferenceCache = cache;
                    cacheTimestampUtc = loadedTimestampUtc;
                    return cache;
                }

                reverseReferenceCache = RebuildCache();
                cacheTimestampUtc = DateTime.UtcNow;
                SaveCache(reverseReferenceCache, cacheTimestampUtc);
                return reverseReferenceCache;
            }
        }

        static Dictionary<string, string[]> RebuildCache()
        {
            var assetGuids = AssetDatabase.FindAssets(string.Empty);
            // asset and dependency GUIDs are unique per source asset, so lists avoid one hash table per target
            var reverseLookup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var dependencyGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var assetGuid in assetGuids)
            {
                if (AssetDatabase.GUIDToAssetPath(assetGuid) is not { Length: > 0 } assetPath)
                    continue;

                foreach (var dependencyPath in AssetDatabase.GetDependencies(assetPath, false))
                {
                    if (dependencyPath == assetPath)
                        continue;

                    if (!dependencyGuids.TryGetValue(dependencyPath, out var dependencyGuid))
                    {
                        dependencyGuid = AssetDatabase.AssetPathToGUID(dependencyPath);
                        dependencyGuids[dependencyPath] = dependencyGuid;
                    }
                    if (dependencyGuid.Length == 0)
                        continue;

                    if (!reverseLookup.TryGetValue(dependencyGuid, out var referencers))
                    {
                        referencers = new();
                        reverseLookup[dependencyGuid] = referencers;
                    }

                    referencers.Add(assetGuid);
                }
            }

            var cache = new Dictionary<string, string[]>(reverseLookup.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in reverseLookup)
            {
                pair.Value.Sort(StringComparer.OrdinalIgnoreCase);
                cache[pair.Key] = pair.Value.ToArray();
            }

            return cache;
        }

        static bool TryLoadCache(out Dictionary<string, string[]>? cache, out DateTime timestampUtc)
        {
            cache = null;
            timestampUtc = DateTime.MinValue;

            if (!File.Exists(CachePath))
                return false;

            if (IsExpired(File.GetLastWriteTimeUtc(CachePath)))
                return false;

            try
            {
                var document = JsonUtility.FromJson<ReferenceCacheDocument>(File.ReadAllText(CachePath));
                if (document == null)
                    return false;

                timestampUtc = DateTime.TryParse(document.cached_at_utc, out var parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : File.GetLastWriteTimeUtc(CachePath);

                if (IsExpired(timestampUtc))
                    return false;

                cache = new(document.entries.Length, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in document.entries)
                    cache[entry.guid] = entry.referencer_guids ?? Array.Empty<string>();

                return true;
            }
            catch (Exception exception)
            {
                ConduitDiagnostics.Error("Failed to load reverse reference cache from disk.", exception);
                return false;
            }
        }

        static void SaveCache(Dictionary<string, string[]> cache, DateTime timestampUtc)
        {
            if (Path.GetDirectoryName(CachePath) is { Length: > 0 } directoryPath)
                Directory.CreateDirectory(directoryPath);

            var keys = new string[cache.Count];
            cache.Keys.CopyTo(keys, 0);

            Array.Sort(keys, StringComparer.OrdinalIgnoreCase);
            var entries = new SerializableLookupEntry[keys.Length];
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                entries[index] = new()
                {
                    guid = key,
                    referencer_guids = cache[key],
                };
            }

            var document = new ReferenceCacheDocument
            {
                cached_at_utc = timestampUtc.ToString("O"),
                entries = entries,
            };

            File.WriteAllText(CachePath, JsonUtility.ToJson(document));
        }

        static bool IsExpired(DateTime timestampUtc)
            => timestampUtc == DateTime.MinValue || timestampUtc < DateTime.UtcNow.AddHours(-24);

        [Serializable]
        sealed class ReferenceCacheDocument
        {
            public string cached_at_utc = string.Empty;
            public SerializableLookupEntry[] entries = Array.Empty<SerializableLookupEntry>();
        }

        [Serializable]
        sealed class SerializableLookupEntry
        {
            public string guid = string.Empty;
            public string[] referencer_guids = Array.Empty<string>();
        }
    }
}
