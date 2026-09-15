#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace Conduit
{
    /// <summary>Preserves managed method bodies independently of their patched native entry points.</summary>
    static class OriginalMethod
    {
        static readonly Dictionary<(Guid, int), Lazy<MethodInfo>> methods = new();
        static readonly Lazy<ModuleBuilder> module = new(CreateModule);

        internal static Delegate CreateDelegate(MethodInfo target, Type delegateType)
        {
            MethodDetourSupport.ValidateSignature(target, delegateType.GetMethod("Invoke")
                ?? throw new ArgumentException("An original-method delegate type is required.", nameof(delegateType)));
            lock (methods)
            {
                var key = (target.Module.ModuleVersionId, target.MetadataToken);
                if (!methods.TryGetValue(key, out var original))
                    methods.Add(key, original = new Lazy<MethodInfo>(() => Clone(target)));

                // cache failures too: rejected bodies must not repeatedly allocate noncollectible types.
                return original.Value.CreateDelegate(delegateType);
            }
        }

        static ModuleBuilder CreateModule()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("Conduit.OriginalMethods"), AssemblyBuilderAccess.Run);
            MonoAssemblyAccess.EnablePrivateAccess(assembly);
            return assembly.DefineDynamicModule("OriginalMethods");
        }

        static MethodInfo Clone(MethodInfo target)
        {
            try
            {
                if ((target.GetMethodImplementationFlags() & MethodImplAttributes.Synchronized) != 0)
                    throw new NotSupportedException("synchronized methods");

                var body = target.GetMethodBody() ?? throw new NotSupportedException("methods without managed IL");
                ValidateType(target.ReturnType);
                var parameters = target.GetParameters();
                foreach (var parameter in parameters)
                    ValidateType(parameter.ParameterType);
                foreach (var local in body.LocalVariables)
                    ValidateType(local.LocalType);
                OriginalMethodIL.ValidateLocals(target.Module, body);
                var instructions = OriginalMethodIL.Read(target, body);
                var exceptions = new OriginalMethodExceptions(body, instructions);

                // finish validation before allocating a type; emitted Mono assemblies live until domain reload.
                var type = module.Value.DefineType("Original" + methods.Count,
                    TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                var method = type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.Static);
                var receiver = target.IsStatic ? Type.EmptyTypes : new[]
                {
                    target.DeclaringType!.IsValueType ? target.DeclaringType.MakeByRefType() : target.DeclaringType
                };
                var receiverModifiers = receiver.Select(_ => Type.EmptyTypes);
                method.SetSignature(target.ReturnType,
                    target.ReturnParameter.GetRequiredCustomModifiers(), target.ReturnParameter.GetOptionalCustomModifiers(),
                    receiver.Concat(parameters.Select(p => p.ParameterType)).ToArray(),
                    receiverModifiers.Concat(parameters.Select(p => p.GetRequiredCustomModifiers())).ToArray(),
                    receiverModifiers.Concat(parameters.Select(p => p.GetOptionalCustomModifiers())).ToArray());
                method.DefineParameter(0, target.ReturnParameter.Attributes, null);
                foreach (var parameter in parameters)
                    method.DefineParameter(parameter.Position + receiver.Length + 1, parameter.Attributes, parameter.Name);
                method.InitLocals = body.InitLocals;
                method.SetImplementationFlags(MethodImplAttributes.NoInlining);
                var il = method.GetILGenerator();
                foreach (var local in body.LocalVariables)
                    il.DeclareLocal(local.LocalType, local.IsPinned);
                var labels = instructions.ToDictionary(i => i.Offset, _ => il.DefineLabel());
                labels.Add(body.GetILAsByteArray()!.Length, il.DefineLabel());
                foreach (var instruction in instructions)
                {
                    exceptions.EmitBoundary(il, instruction.Offset);
                    il.MarkLabel(labels[instruction.Offset]);
                    if (!exceptions.IsImplicitTerminator(instruction.Offset))
                        OriginalMethodIL.Emit(il, instruction, labels);
                }
                int end = body.GetILAsByteArray()!.Length;
                exceptions.EmitBoundary(il, end);
                il.MarkLabel(labels[end]);
                var clone = type.CreateType()!.GetMethod("Invoke")!;
                RuntimeHelpers.PrepareMethod(clone.MethodHandle); // fail before the target's entry point changes
                clone.MethodHandle.GetFunctionPointer();
                return clone;
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException
                                              or InvalidProgramException or BadImageFormatException or TypeLoadException)
            {
                throw new NotSupportedException($"Cannot call the original '{target.DeclaringType}.{target.Name}': {exception.Message}", exception);
            }
        }

        internal static void ValidateType(Type type)
        {
            if (MonoSignature.IsFunctionPointer(type))
                throw new NotSupportedException("function-pointer signatures and locals");
            if (type.HasElementType)
                ValidateType(type.GetElementType()!);
            foreach (var argument in type.GetGenericArguments())
                ValidateType(argument);
        }
    }
}
