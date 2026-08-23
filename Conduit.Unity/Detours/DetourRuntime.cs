#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Conduit
{
    static class DetourRuntime
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

        static string Test(
            (Guid ModuleVersionId, int MetadataToken) key,
            MethodInfo target,
            string signatureHash,
            string canonicalName,
            string declaration,
            MethodInfo replacement)
        {
            var targetCode = MonoJit.GetCode(target);
            var replacementCode = MonoJit.GetCode(replacement);
            var existing = GetActive(key);
            var plan = NativePatch.Plan(targetCode, replacementCode.Start, existing?.Patch.Original);
            var result = "Detourable: yes\n"
                   + $"Method: {canonicalName}\n"
                   + $"Identity: MVID {key.ModuleVersionId:N}, token 0x{key.MetadataToken:x8}, signature {signatureHash}\n"
                   + $"Replacement: {declaration}\n"
                   + $"Target JIT body: {targetCode.Size} bytes\n"
                   + $"Jump encoding: {(plan.Kind == PatchKind.Relative ? "5-byte relative" : "14-byte absolute")}\n"
                   + $"Active detour: {(existing == null ? "no" : "yes (" + existing.DisplayName + ")")}\n"
                   + "Original-call delegate: unavailable";
            return result + GetInliningWarning(target);
        }

        static string Apply(
            (Guid ModuleVersionId, int MetadataToken) key,
            MethodInfo target,
            string signatureHash,
            string canonicalName,
            string declaration,
            MethodInfo replacement,
            byte[] assemblyBytes,
            byte[]? pdbBytes,
            string generatedTypeName,
            string displayName)
        {
            lock (gate)
            {
                var targetCode = MonoJit.GetCode(target);
                var replacementCode = MonoJit.GetCode(replacement);
                active.TryGetValue(key, out var existing);
                var plan = NativePatch.Plan(targetCode, replacementCode.Start, existing?.Patch.Original);
                if (existing == null)
                    NativePatch.Install(
                        new(plan.Address, plan.Original, plan.Original, plan.Kind),
                        plan
                    );
                else
                    NativePatch.Install(existing.Patch, plan);

                active[key] = new(
                    key,
                    replacement,
                    plan,
                    signatureHash,
                    canonicalName,
                    declaration,
                    assemblyBytes,
                    pdbBytes,
                    generatedTypeName,
                    displayName
                );
                RebuildActiveMethodNames();
                var result = existing == null
                    ? $"Detoured {canonicalName} with {displayName}."
                    : $"Updated detour for {canonicalName} with {displayName}.";
                return result + GetInliningWarning(target);
            }
        }

        internal static string GetInliningWarning(MethodInfo target)
        {
            var implementation = target.GetMethodImplementationFlags();
            if ((implementation & MethodImplAttributes.NoInlining) != 0)
                return string.Empty;

            var aggressive = (implementation & MethodImplAttributes.AggressiveInlining) != 0;
            var triviallyInlineable = target.GetMethodBody()?.GetILAsByteArray() is { Length: <= 16 };
            if (!aggressive && !triviallyInlineable)
                return string.Empty;

            return "\nWarning: this method may be inlined; already-compiled direct calls can bypass the detour.";
        }

        static string Restore(
            (Guid ModuleVersionId, int MetadataToken) key,
            string canonicalName)
        {
            lock (gate)
            {
                if (!active.TryGetValue(key, out var detour))
                    return $"No detour is applied to {canonicalName} in the current domain lifetime.";
                NativePatch.Restore(detour.Patch);
                active.Remove(key);
                RebuildActiveMethodNames();
                return $"Restored the original implementation of {canonicalName}.";
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

        static MethodInfo LoadReplacement(
            byte[]? assemblyBytes,
            byte[]? pdbBytes,
            string? generatedTypeName)
        {
            if (assemblyBytes == null || string.IsNullOrWhiteSpace(generatedTypeName))
                throw new InvalidOperationException("The MCP server did not provide a compiled detour assembly.");

            var assembly = CompiledAssembly.Load(assemblyBytes, pdbBytes);

            var type = assembly.GetType(generatedTypeName, throwOnError: true)
                       ?? throw new TypeLoadException($"Generated detour type '{generatedTypeName}' was not found.");
            var method = type.GetMethod("Replace", BindingFlags.Public | BindingFlags.Static)
                         ?? throw new MissingMethodException(type.FullName, "Replace");
            var accessProbe = type.GetMethod("AccessProbe", BindingFlags.Public | BindingFlags.Static)
                              ?? throw new MissingMethodException(type.FullName, "AccessProbe");
            // validate Mono's private-access flag before a native entry point can be changed.
            MonoAssemblyAccess.EnablePrivateAccess(assembly);
            var probeValue = accessProbe.Invoke(null, null);
            if (!Equals(probeValue, DetourAccessProbe.ExpectedValue))
                throw new MethodAccessException("The generated detour assembly failed its private-access probe.");
            return method;
        }

        static MethodInfo ResolveTarget(Guid moduleVersionId, int metadataToken)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            foreach (var module in assembly.GetModules())
            {
                if (module.ModuleVersionId != moduleVersionId)
                    continue;
                try
                {
                    return module.ResolveMethod(metadataToken) as MethodInfo
                           ?? throw new NotSupportedException("The selected metadata token is not a method.");
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidOperationException(
                        "The target metadata changed after compilation; run detour again.",
                        exception
                    );
                }
            }

            throw new InvalidOperationException(
                $"Loaded target module '{moduleVersionId:N}' was not found; scripts may have recompiled."
            );
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

    sealed class DetourSnapshot
    {
        internal string ModuleVersionId = string.Empty;
        internal string MetadataToken = string.Empty;
        internal string SignatureHash = string.Empty;
        internal string CanonicalName = string.Empty;
        internal string Declaration = string.Empty;
        internal byte[] AssemblyBytes = Array.Empty<byte>();
        internal byte[]? PdbBytes;
        internal string GeneratedTypeName = string.Empty;
        internal string DisplayName = string.Empty;
    }
}
