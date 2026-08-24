#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Conduit
{
    static partial class DetourRuntime
    {
        static readonly object gate = new();
        static readonly Dictionary<(Guid ModuleVersionId, int MetadataToken), ActiveDetour> active = new();
        static string[] activeMethodNames = Array.Empty<string>();

        internal static int ActiveCount
        {
            get
            {
                lock (gate)
                    return active.Count;
            }
        }

        internal static string[] ActiveMethodNames
        {
            get
            {
                lock (gate)
                    return activeMethodNames;
            }
        }

        internal static string Execute(
            string mode,
            string mvid,
            string token,
            string signatureHash,
            string canonicalName,
            string declaration,
            byte[]? assemblyBytes,
            byte[]? pdbBytes,
            string? generatedTypeName,
            string? displayName)
        {
            if (!Guid.TryParseExact(mvid, "N", out var moduleVersionId))
                throw new ArgumentException("The detour target MVID is invalid.", nameof(mvid));
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metadataToken))
                throw new ArgumentException("The detour target metadata token is invalid.", nameof(token));

            var target = ResolveTarget(moduleVersionId, metadataToken);
            MethodDetourSupport.Validate(target);
            var key = (ModuleVersionId: moduleVersionId, MetadataToken: metadataToken);
            return mode switch
            {
                "restore" => Restore(key, canonicalName),
                "test" => Test(
                    key,
                    target,
                    signatureHash,
                    canonicalName,
                    declaration,
                    LoadReplacement(assemblyBytes, pdbBytes, generatedTypeName)
                ),
                "apply" => Apply(
                    key,
                    target,
                    signatureHash,
                    canonicalName,
                    declaration,
                    LoadReplacement(assemblyBytes, pdbBytes, generatedTypeName),
                    assemblyBytes!,
                    pdbBytes,
                    generatedTypeName!,
                    displayName ?? "detour"
                ),
                _ => throw new ArgumentException($"Unknown detour mode '{mode}'.", nameof(mode)),
            };
        }

        internal static DetourSnapshot[] GetSnapshots()
        {
            lock (gate)
            {
                var snapshots = new DetourSnapshot[active.Count];
                var index = 0;
                foreach (var detour in active.Values)
                    snapshots[index++] = detour.ToSnapshot();
                return snapshots;
            }
        }

        internal static void Reapply(DetourSnapshot snapshot)
        {
            Execute(
                "apply",
                snapshot.ModuleVersionId,
                snapshot.MetadataToken,
                snapshot.SignatureHash,
                snapshot.CanonicalName,
                snapshot.Declaration,
                snapshot.AssemblyBytes,
                snapshot.PdbBytes,
                snapshot.GeneratedTypeName,
                snapshot.DisplayName
            );
        }

        internal static int RestoreAll()
        {
            lock (gate)
            {
                int restored = 0;
                List<Exception>? failures = null;
                foreach (var pair in active.ToArray())
                {
                    try
                    {
                        NativePatch.Restore(pair.Value.Patch);
                        active.Remove(pair.Key);
                        restored++;
                    }
                    catch (Exception exception)
                    {
                        (failures ??= new()).Add(exception);
                    }
                }

                RebuildActiveMethodNames();

                if (failures is { Count: > 0 })
                    throw new AggregateException("One or more method detours could not be restored.", failures);
                return restored;
            }
        }

        static void RebuildActiveMethodNames()
        {
            if (active.Count == 0)
            {
                activeMethodNames = Array.Empty<string>();
                return;
            }

            var names = new string[active.Count];
            var index = 0;
            foreach (var detour in active.Values)
                names[index++] = detour.CanonicalName;

            Array.Sort(names, StringComparer.Ordinal);
            activeMethodNames = names;
        }

        static ActiveDetour? GetActive((Guid ModuleVersionId, int MetadataToken) key)
        {
            lock (gate)
                return active.TryGetValue(key, out var detour) ? detour : null;
        }

        sealed class ActiveDetour
        {
            internal ActiveDetour(
                (Guid ModuleVersionId, int MetadataToken) key,
                MethodInfo replacementMethod,
                PatchPlan patch,
                string signatureHash,
                string canonicalName,
                string declaration,
                byte[] assemblyBytes,
                byte[]? pdbBytes,
                string generatedTypeName,
                string displayName)
            {
                Key = key;
                ReplacementMethod = replacementMethod; // keeps the generated JIT body alive
                Patch = patch;
                SignatureHash = signatureHash;
                CanonicalName = canonicalName;
                Declaration = declaration;
                AssemblyBytes = assemblyBytes;
                PdbBytes = pdbBytes;
                GeneratedTypeName = generatedTypeName;
                DisplayName = displayName;
            }

            internal (Guid ModuleVersionId, int MetadataToken) Key { get; }
            internal MethodInfo ReplacementMethod { get; }
            internal PatchPlan Patch { get; }
            internal string SignatureHash { get; }
            internal string CanonicalName { get; }
            internal string Declaration { get; }
            internal byte[] AssemblyBytes { get; }
            internal byte[]? PdbBytes { get; }
            internal string GeneratedTypeName { get; }
            internal string DisplayName { get; }

            internal DetourSnapshot ToSnapshot() =>
                new()
                {
                    ModuleVersionId = Key.ModuleVersionId.ToString("N"),
                    MetadataToken = Key.MetadataToken.ToString(CultureInfo.InvariantCulture),
                    SignatureHash = SignatureHash,
                    CanonicalName = CanonicalName,
                    Declaration = Declaration,
                    AssemblyBytes = AssemblyBytes,
                    PdbBytes = PdbBytes,
                    GeneratedTypeName = GeneratedTypeName,
                    DisplayName = DisplayName,
                };
        }
    }
}
