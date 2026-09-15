#nullable enable

using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Conduit;
using NUnit.Framework;

public sealed partial class DetourRuntimeTests
{
    [Test]
    public void OriginalCloneRunsFaultHandlersOnlyOnException()
    {
        string name = "ConduitOriginalFault_" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), name + ".dll");
        try
        {
            var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
                new AssemblyName(name), AssemblyBuilderAccess.RunAndSave, Path.GetTempPath());
            var module = assembly.DefineDynamicModule(name, name + ".dll");
            var type = module.DefineType("Fixture", TypeAttributes.Public);
            var count = type.DefineField("Faults", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
            var method = type.DefineMethod("Fault", MethodAttributes.Public | MethodAttributes.Static, typeof(int), new[] { typeof(int) });
            var il = method.GetILGenerator();
            var result = il.DeclareLocal(typeof(int));
            var normal = il.DefineLabel();
            var end = il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Bge_S, normal);
            il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Throw);
            il.MarkLabel(normal);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Leave_S, end);
            il.BeginFaultBlock();
            il.Emit(OpCodes.Ldsfld, count);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stsfld, count);
            il.EndExceptionBlock();
            il.Emit(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Ret);
            type.CreateType();
            assembly.Save(name + ".dll");
            var loaded = Assembly.Load(File.ReadAllBytes(path)).GetType("Fixture")!;
            var target = loaded.GetMethod("Fault")!;
            var direct = (Func<int, int>)target.CreateDelegate(typeof(Func<int, int>));
            var original = (Func<int, int>)OriginalMethod.CreateDelegate(target, typeof(Func<int, int>));
            Assert.That(original(7), Is.EqualTo(direct(7)));
            var faults = loaded.GetField("Faults")!;
            Assert.That(faults.GetValue(null), Is.EqualTo(0));
            Assert.Throws<InvalidOperationException>(() => direct(-1));
            int expected = (int)faults.GetValue(null)!;
            faults.SetValue(null, 0);
            Assert.Throws<InvalidOperationException>(() => original(-1));
            Assert.That(faults.GetValue(null), Is.EqualTo(expected));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void OriginalCloneRejectsIndirectCallsWithoutDisablingOrdinaryDetours()
    {
        var target = Method(nameof(OriginalIndirect));
        int expected = OriginalIndirect(4);
        Assert.Throws<NotSupportedException>(() => OriginalMethod.CreateDelegate(target, typeof(Func<int, int>)));
        using (new MethodDetour(target, Method(nameof(OriginalConstant))))
            Assert.That(OriginalIndirect(4), Is.EqualTo(OriginalConstant(4)));
        Assert.That(OriginalIndirect(4), Is.EqualTo(expected));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int OriginalIndirect(int value)
    {
        delegate*<int, int> function = &OriginalConstant;
        return function(value);
    }
}
