#nullable enable

using System;
using System.Globalization;
using System.Reflection;

namespace Conduit
{
    static partial class DetourRuntime
    {
        static string Test(
            (Guid ModuleVersionId, int MetadataToken) key,
            MethodInfo target,
            string canonicalName,
            string declaration,
            MethodInfo replacement)
        {
            MethodDetourSupport.ValidateReplacement(target, replacement);
            // planning compiles both bodies and checks jump reach without writing into the target.
            var targetCode = MonoJit.GetCode(target);
            var replacementCode = MonoJit.GetCode(replacement);
            var existing = GetActive(key);
            NativePatch.Plan(targetCode, replacementCode.Start, existing?.Patch.Original); // validates without changing the target
            var result = $"Method: {canonicalName}\n"
                   + "Detourable: yes\n"
                   + $"Generated replacement signature: {declaration}\n"
                   + $"Active detour: {(existing == null ? "no" : "yes (" + existing.DisplayName + ")")}";
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
            MethodDetourSupport.ValidateReplacement(target, replacement);
            lock (gate)
            {
                active.TryGetValue(key, out var existing);
                if (existing is { Snapshot: null })
                    throw new InvalidOperationException($"{canonicalName} has a caller-owned detour; restore it before applying another.");

                var snapshot = new DetourSnapshot
                {
                    ModuleVersionId = key.ModuleVersionId.ToString("N"),
                    MetadataToken = key.MetadataToken.ToString(CultureInfo.InvariantCulture),
                    SignatureHash = signatureHash,
                    CanonicalName = canonicalName,
                    Declaration = declaration,
                    AssemblyBytes = assemblyBytes,
                    PdbBytes = pdbBytes,
                    GeneratedTypeName = generatedTypeName,
                    DisplayName = displayName,
                };
                Install(target, replacement, canonicalName, displayName, existing, snapshot);
                var result = existing == null
                    ? $"Detoured {canonicalName} with {displayName}."
                    : $"Updated detour for {canonicalName} with {displayName}.";
                return result + GetInliningWarning(target);
            }
        }

        internal static ActiveDetour Apply(MethodInfo target, MethodInfo replacement)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            MethodDetourSupport.Validate(target);
            MethodDetourSupport.ValidateReplacement(target, replacement);

            lock (gate)
            {
                var name = target.DeclaringType!.FullName + "." + target.Name;
                if (active.ContainsKey((target.Module.ModuleVersionId, target.MetadataToken)))
                    throw new InvalidOperationException($"{name} already has an active detour; restore it before applying another.");
                return Install(target, replacement, name, replacement.DeclaringType!.FullName + "." + replacement.Name);
            }
        }

        // both entry points hold gate so native writes and ownership changes form one operation.
        static ActiveDetour Install(
            MethodInfo target,
            MethodInfo replacement,
            string canonicalName,
            string displayName,
            ActiveDetour? existing = null,
            DetourSnapshot? snapshot = null)
        {
            var targetCode = MonoJit.GetCode(target);
            var replacementCode = MonoJit.GetCode(replacement);
            // updates must retain the first implementation's bytes, not save the jump being replaced.
            var plan = NativePatch.Plan(targetCode, replacementCode.Start, existing?.Patch.Original);
            var key = (target.Module.ModuleVersionId, target.MetadataToken);
            var detour = new ActiveDetour(key, replacement, plan, canonicalName, displayName, snapshot);
            NativePatch.Install(existing?.Patch ?? new(plan.Address, plan.Original, plan.Original, plan.Kind), plan);
            active[key] = detour;
            RebuildActiveMethodNames();
            return detour;
        }

        internal static void Restore(ActiveDetour detour)
        {
            lock (gate)
            {
                // an old handle can outlive global cleanup and a later replacement of the same target.
                if (!active.TryGetValue(detour.Key, out var current) || !ReferenceEquals(current, detour))
                    return;
                NativePatch.Restore(detour.Patch);
                active.Remove(detour.Key);
                RebuildActiveMethodNames();
            }
        }

        internal static string GetInliningWarning(MethodInfo target)
        {
            var implementation = target.GetMethodImplementationFlags();
            if ((implementation & MethodImplAttributes.NoInlining) != 0)
                return string.Empty;

            // entry-point patches cannot replace copies of the body already embedded in callers.
            // the IL size is only a warning heuristic; Mono's actual inlining decisions are not available here.
            bool aggressive = (implementation & MethodImplAttributes.AggressiveInlining) != 0;
            bool triviallyInlineable = target.GetMethodBody()?.GetILAsByteArray() is { Length: <= 16 };
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
    }
}
