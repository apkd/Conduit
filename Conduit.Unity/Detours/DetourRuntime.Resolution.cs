#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    static partial class DetourRuntime
    {
        internal static (MethodInfo Target, MethodInfo Replacement) ResolveMethods(
            Type targetType,
            string targetMethodName,
            Type replacementType,
            string replacementMethodName,
            Type[]? parameterTypes)
        {
            var target = FindMethod(targetType, targetMethodName, parameterTypes);
            var parameters = target.GetParameters();
            int offset = target.IsStatic ? 0 : 1;
            var replacementParameters = new Type[parameters.Length + offset];
            // instance entry points receive a hidden receiver; the replacement names it explicitly.
            if (offset != 0)
            {
                var receiver = target.DeclaringType!;
                replacementParameters[0] = receiver.IsValueType ? receiver.MakeByRefType() : receiver;
            }
            foreach (var parameter in parameters)
                replacementParameters[parameter.Position + offset] = parameter.ParameterType;
            return (target, FindMethod(replacementType, replacementMethodName, replacementParameters));
        }

        internal static (MethodInfo Target, MethodInfo Replacement) ResolveMethods(
            string targetMethodName,
            string replacementMethodName,
            Type[]? parameterTypes)
        {
            var target = SplitName(targetMethodName);
            var replacement = SplitName(replacementMethodName);
            return ResolveMethods(target.Type, target.Method, replacement.Type, replacement.Method, parameterTypes);

            static (Type Type, string Method) SplitName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A fully qualified method name is required.", nameof(name));
                int separator = name.LastIndexOf('.');
                if (separator <= 0 || separator == name.Length - 1)
                    throw new ArgumentException("Use Namespace.Type.Method, with + between nested type names.", nameof(name));
                var typeName = name.Substring(0, separator);
                Type? type = null;
                // exact assembly lookups avoid scanning every type or depending on the reflection UI layer.
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var candidate = assembly.GetType(typeName, throwOnError: false);
                    if (candidate == null || candidate == type)
                        continue;
                    if (type != null)
                        throw new AmbiguousMatchException($"Multiple loaded types are named '{typeName}'; use the Type overload.");
                    type = candidate;
                }
                return (type ?? throw new TypeLoadException($"Loaded type '{typeName}' was not found."), name.Substring(separator + 1));
            }
        }

        static MethodInfo FindMethod(Type type, string name, Type[]? parameterTypes)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("An exact method name is required.", nameof(name));

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            MethodInfo? found = null;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name != name)
                    continue;
                if (parameterTypes != null && !ParametersMatch(method, parameterTypes))
                    continue;
                if (found != null)
                    throw new AmbiguousMatchException($"Multiple methods match '{type.FullName}.{name}'; specify parameter types or use MethodInfo.");
                found = method;
            }
            return found ?? throw new MissingMethodException(type.FullName, name);

            static bool ParametersMatch(MethodInfo method, Type[] types)
            {
                var parameters = method.GetParameters();
                if (parameters.Length != types.Length)
                    return false;
                foreach (var parameter in parameters)
                    if (types[parameter.Position] == null
                        || !MethodDetourSupport.TypesMatch(parameter.ParameterType, types[parameter.Position]))
                        return false;
                return true;
            }
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
            // generated code can reference private game members; verify that Mono will permit those calls
            // before installing a jump that could expose a failing replacement to unrelated callers.
            MonoAssemblyAccess.EnablePrivateAccess(assembly);
            var probeValue = accessProbe.Invoke(null, null);
            if (!Equals(probeValue, DetourAccessProbe.ExpectedValue))
                throw new MethodAccessException("The generated detour assembly failed its private-access probe.");
            return method;
        }

        static MethodInfo ResolveTarget(Guid moduleVersionId, int metadataToken)
        {
            // compilation on the MCP server may span a script reload. Resolve the exact module version, rather than
            // silently patching a same-named method whose signature or metadata token may have changed.
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
