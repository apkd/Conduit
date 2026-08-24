#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit
{
    static class ProjectSettingsTypeResolver
    {
        static readonly object resolvedTypeCacheGate = new();
        static readonly Dictionary<string, Type?> resolvedTypeCache = new(StringComparer.Ordinal);
        static ProjectSettingsTypeResolver()
            => AppDomain.CurrentDomain.AssemblyLoad += static (_, args) =>
            {
                try
                {
                    // generated snippets cannot supply Unity package settings types.
                    if (args.LoadedAssembly.IsDynamic
                        || string.IsNullOrWhiteSpace(args.LoadedAssembly.Location))
                        return;
                }
                catch (Exception) { }

                lock (resolvedTypeCacheGate)
                {
                    var missing = resolvedTypeCache
                        .Where(static pair => pair.Value == null)
                        .Select(static pair => pair.Key)
                        .ToArray();
                    foreach (var fullName in missing)
                        resolvedTypeCache.Remove(fullName);
                }
            };
        internal static Type? Resolve(string fullName)
        {
            lock (resolvedTypeCacheGate)
            {
                if (resolvedTypeCache.TryGetValue(fullName, out var cached))
                    return cached;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetType(fullName, throwOnError: false) is { } type)
                        return resolvedTypeCache[fullName] = type;

                string shortName = fullName[(fullName.LastIndexOf('.') + 1)..];
                return resolvedTypeCache[fullName] = UnityEditor.TypeCache
                    .GetTypesDerivedFrom<UnityEngine.ScriptableObject>()
                    .FirstOrDefault(type => type.FullName == fullName || type.Name == shortName);
            }
        }
    }
}
