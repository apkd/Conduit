#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    static partial class DetourRuntime
    {
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
    }
}

