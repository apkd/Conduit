#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    static partial class DetourRuntime
    {
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
    }
}

