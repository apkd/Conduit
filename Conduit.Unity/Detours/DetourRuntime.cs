#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Conduit
{
    /// <summary>Owns active method replacements shared by the C# API, MCP commands, and editor cleanup.</summary>
    /// <remarks>
    /// Registration and native writes share one lock. Each record retains the replacement method and the
    /// original native bytes, so updating an MCP detour still restores the implementation from before the
    /// first patch. Only MCP records contain a snapshot: caller-owned methods depend on their current domain.
    /// The lock coordinates patch owners; it cannot stop other threads from executing the patched method.
    /// </remarks>
    static partial class DetourRuntime
    {
        static readonly object gate = new();
        // tokens identify methods within a module; the MVID keeps separately compiled modules distinct.
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

        // status polling reuses this array; callers must treat it as read-only.
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
                    canonicalName,
                    declaration,
                    LoadReplacement(target, assemblyBytes, pdbBytes, generatedTypeName)
                ),
                "apply" => Apply(
                    key,
                    target,
                    signatureHash,
                    canonicalName,
                    declaration,
                    LoadReplacement(target, assemblyBytes, pdbBytes, generatedTypeName),
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
                var snapshots = new List<DetourSnapshot>();
                foreach (var detour in active.Values)
                    // caller-owned replacements depend on state that is lost with their domain.
                    if (detour.Snapshot is { } snapshot)
                        snapshots.Add(snapshot);
                return snapshots.ToArray();
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
                    // retain failed records so callers can retry without losing the original bytes.
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
            int index = 0;
            foreach (var detour in active.Values)
                names[index++] = detour.CanonicalName;

            Array.Sort(names, StringComparer.Ordinal); // stable status output without sorting on each read
            activeMethodNames = names;
        }

        static ActiveDetour? GetActive((Guid ModuleVersionId, int MetadataToken) key)
        {
            lock (gate)
                return active.TryGetValue(key, out var detour) ? detour : null;
        }

        /// <summary>Retains the installed code and acts as the ownership token held by a disposable handle.</summary>
        internal sealed class ActiveDetour
        {
            internal ActiveDetour(
                (Guid ModuleVersionId, int MetadataToken) key,
                MethodInfo replacementMethod,
                PatchPlan patch,
                string canonicalName,
                string displayName,
                DetourSnapshot? snapshot = null)
            {
                Key = key;
                ReplacementMethod = replacementMethod; // keeps the generated JIT body alive
                Patch = patch;
                CanonicalName = canonicalName;
                DisplayName = displayName;
                Snapshot = snapshot;
            }

            internal (Guid ModuleVersionId, int MetadataToken) Key { get; }
            internal MethodInfo ReplacementMethod { get; }
            internal PatchPlan Patch { get; }
            internal string CanonicalName { get; }
            internal string DisplayName { get; }
            internal DetourSnapshot? Snapshot { get; }
        }
    }
}
