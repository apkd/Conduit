#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Conduit
{
    /// <summary>Rejects methods and replacement signatures that cannot share a native calling convention safely.</summary>
    /// <remarks>
    /// A native jump does no argument conversion and supplies no generic context. Matching managed signatures
    /// before patching prevents the replacement from reading arguments or returning values in the wrong form.
    /// Inspection also uses these rules when describing targets that the server can reproduce in generated C#.
    /// </remarks>
    static class MethodDetourSupport
    {
        internal enum MethodRole { Target, Replacement }

        public static string? GetUnsupportedReason(MethodBase method, MethodRole role = MethodRole.Target)
        {
            if (Type.GetType("Mono.Runtime") == null
                || RuntimeInformation.ProcessArchitecture != Architecture.X64
                || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "runtime method detouring supports Unity Mono on Windows/Linux x64 only";
            if (method is ConstructorInfo)
                return "constructors are not supported";
            // closed generic instantiations can share native code and the same module/token identity.
            if (method.IsGenericMethod || method.DeclaringType?.IsGenericType == true)
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
            if (method.Module.Assembly.IsDynamic
                || role == MethodRole.Target && string.IsNullOrWhiteSpace(TryGetLocation(method.Module.Assembly)))
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
                // metadata permits names that generated C# cannot spell, including compiler-generated types.
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

        internal static void ValidateReplacement(MethodInfo target, MethodInfo replacement)
        {
            if (replacement == null)
                throw new ArgumentNullException(nameof(replacement));
            if (target.Equals(replacement))
                throw new ArgumentException("A method cannot replace itself.", nameof(replacement));
            if (!replacement.IsStatic)
                throw new ArgumentException("The replacement must be static, with an explicit receiver for instance targets.", nameof(replacement));
            // generated MCP replacements are loaded from verified assembly bytes and have no file location.
            if (GetUnsupportedReason(replacement, MethodRole.Replacement) is { } reason)
                throw new NotSupportedException(reason);

            ValidateSignature(target, replacement);
        }

        internal static void ValidateSignature(MethodInfo target, MethodInfo replacement)
        {
            bool delegateSignature = typeof(MulticastDelegate).IsAssignableFrom(replacement.DeclaringType);
            var expected = target.GetParameters();
            var actual = replacement.GetParameters();
            int offset = target.IsStatic ? 0 : 1;
            if (actual.Length != expected.Length + offset || !Matches(target.ReturnParameter, replacement.ReturnParameter))
                throw Mismatch();
            if (offset != 0)
            {
                var receiver = target.DeclaringType!;
                if (receiver.IsValueType)
                    receiver = receiver.MakeByRefType();
                if (!TypesMatch(receiver, actual[0].ParameterType) || actual[0].IsIn || actual[0].IsOut
                    || actual[0].GetRequiredCustomModifiers().Length != 0)
                    throw Mismatch();
            }
            foreach (var parameter in expected)
                if (!Matches(parameter, actual[parameter.Position + offset]))
                    throw Mismatch();

            ArgumentException Mismatch() => new(
                $"Replacement '{replacement}' does not match '{target}', including its receiver and by-reference parameters.",
                nameof(replacement)
            );

            // int& alone cannot distinguish ref, out, in, or a readonly return.
            bool Matches(ParameterInfo left, ParameterInfo right)
                => TypesMatch(left.ParameterType, right.ParameterType)
                   && left.IsIn == right.IsIn
                   && left.IsOut == right.IsOut
                   && Modifiers(left).SequenceEqual(Modifiers(right));

            // C# adds modreq(InAttribute) to delegate in parameters, but not to ordinary method in parameters.
            IEnumerable<Type> Modifiers(ParameterInfo parameter)
                => parameter.GetRequiredCustomModifiers().Where(modifier => !delegateSignature || !parameter.IsIn
                    || modifier.FullName != "System.Runtime.InteropServices.InAttribute");
        }

        internal static bool TypesMatch(Type left, Type right)
        {
            if (left.HasElementType || right.HasElementType)
                return left.HasElementType && right.HasElementType
                       && left.IsByRef == right.IsByRef
                       && left.IsPointer == right.IsPointer
                       && left.IsArray == right.IsArray
                       && (!left.IsArray || left == right)
                       && TypesMatch(left.GetElementType()!, right.GetElementType()!);

            // mono reports different function-pointer signatures through the same fake managed class.
            if (MonoSignature.IsFunctionPointer(left) || MonoSignature.IsFunctionPointer(right))
                return MonoSignature.IsFunctionPointer(left) && MonoSignature.IsFunctionPointer(right)
                       && MonoSignature.FunctionPointersMatch(left, right);
            return left == right;
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
