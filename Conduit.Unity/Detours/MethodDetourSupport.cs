#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Conduit
{
    /// <summary>Describes intrinsic method-shape limitations shared by inspection and runtime detouring.</summary>
    static class MethodDetourSupport
    {
        public static string? GetUnsupportedReason(MethodBase method)
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64
                || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "runtime method detouring supports Unity Mono on Windows/Linux x64 only";
            if (method is ConstructorInfo)
                return "constructors are not supported";
            if (method.IsGenericMethod || method.DeclaringType?.ContainsGenericParameters == true)
                return "generic methods and methods declared on generic types are not supported";
            if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                return "P/Invoke methods are not supported";

            var implementation = method.GetMethodImplementationFlags();
            if ((implementation & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native)) != 0)
                return "runtime, native, and InternalCall methods are not supported";
            if (method.IsAbstract || !HasManagedBody(method))
                return "the method has no managed implementation body";
            if ((method.CallingConvention & CallingConventions.VarArgs) != 0)
                return "varargs methods are not supported";
            if (method.Module.Assembly.IsDynamic || string.IsNullOrWhiteSpace(TryGetLocation(method.Module.Assembly)))
                return "methods from dynamic or locationless assemblies are not supported";

            if (method is not MethodInfo methodInfo)
                return null;

            if (!methodInfo.IsStatic && FindUnsupportedType(methodInfo.DeclaringType) is { } declaringTypeReason)
                return declaringTypeReason;
            if (FindUnsupportedType(methodInfo.ReturnType) is { } returnTypeReason)
                return returnTypeReason;
            foreach (var parameter in methodInfo.GetParameters())
                if (FindUnsupportedType(parameter.ParameterType) is { } parameterTypeReason)
                    return parameterTypeReason;

            var unsupportedModifier = FindUnsupportedRequiredModifier(methodInfo.ReturnParameter);
            if (unsupportedModifier != null)
                return $"required custom modifier '{unsupportedModifier}' cannot be represented exactly in C#";

            foreach (var parameter in methodInfo.GetParameters())
            {
                unsupportedModifier = FindUnsupportedRequiredModifier(parameter);
                if (unsupportedModifier != null)
                    return $"required custom modifier '{unsupportedModifier}' cannot be represented exactly in C#";
            }

            return null;
        }

        static string? FindUnsupportedType(Type? type)
        {
            if (type == null)
                return "the declaring type is unavailable";
            if (type.IsByRef || type.IsPointer || type.IsArray)
                return FindUnsupportedType(type.GetElementType());
            if (MonoSignature.IsFunctionPointer(type))
                return MonoSignature.GetUnsupportedReason(type);

            for (var current = type; current != null; current = current.DeclaringType)
            {
                var name = current.Name;
                var arity = name.IndexOf('`');
                if (arity >= 0)
                    name = name.Substring(0, arity);
                if (!CSharpIdentifier.IsValid(name))
                    return $"metadata type name '{current.Name}' cannot be represented in C#";
            }

            if (type.Namespace is { Length: > 0 } @namespace)
                foreach (var segment in @namespace.Split('.'))
                    if (!CSharpIdentifier.IsValid(segment))
                        return $"metadata namespace '{@namespace}' cannot be represented in C#";

            if (!type.IsGenericType)
                return null;
            foreach (var argument in type.GetGenericArguments())
                if (FindUnsupportedType(argument) is { } reason)
                    return reason;
            return null;
        }

        public static void Validate(MethodInfo method)
        {
            if (GetUnsupportedReason(method) is { } reason)
                throw new NotSupportedException(reason);
        }

        static bool HasManagedBody(MethodBase method)
        {
            try
            {
                return method.GetMethodBody() != null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }

        static string? FindUnsupportedRequiredModifier(ParameterInfo parameter)
        {
            Type[] modifiers;
            try
            {
                modifiers = parameter.GetRequiredCustomModifiers();
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return null;
            }

            foreach (var modifier in modifiers)
            {
                var name = modifier.FullName ?? modifier.Name;
                if (name is "System.Runtime.CompilerServices.IsReadOnlyAttribute"
                    or "System.Runtime.InteropServices.InAttribute"
                    or "System.Runtime.CompilerServices.RequiresLocationAttribute"
                    || name.StartsWith("System.Runtime.CompilerServices.CallConv", StringComparison.Ordinal))
                    continue;

                return name;
            }

            return null;
        }

        static string TryGetLocation(Assembly assembly)
        {
            try
            {
                return assembly.Location;
            }
            catch (Exception exception) when (exception is NotSupportedException or FileNotFoundException)
            {
                return string.Empty;
            }
        }
    }
}
