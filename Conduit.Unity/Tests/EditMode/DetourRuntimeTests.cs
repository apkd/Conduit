#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Conduit;
using NUnit.Framework;

public sealed class DetourRuntimeTests
{
    static int targetStorage = 11;
    static int replacementStorage = 29;

    [Test]
    public void NativePatch_ReplacesAndRestoresMonoMethodEntry()
    {
        Assert.That(Target(1), Is.EqualTo(2));
        WithPatch(nameof(Target), nameof(Replacement), () =>
            Assert.That(Target(1), Is.EqualTo(101))
        );
        Assert.That(Target(1), Is.EqualTo(2));
    }

    [Test]
    public void NativePatch_UsesAbsoluteEncodingForFarDestination()
    {
        var original = new byte[14];
        var plan = NativePatch.Plan(
            new JitCode(new IntPtr(0x1000), 14),
            new IntPtr(long.MaxValue - 0x1000),
            original
        );

        Assert.That(plan.Kind, Is.EqualTo(PatchKind.Absolute));
        Assert.That(plan.Installed[0], Is.EqualTo(0xff));
        Assert.That(plan.Installed[1], Is.EqualTo(0x25));
    }

    [Test]
    public void DetourRuntimeWarnsForTriviallyInlineableTargets()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;

        Assert.That(
            DetourRuntime.GetInliningWarning(typeof(DetourRuntimeTests).GetMethod(nameof(Increment), flags)!),
            Does.Contain("already-compiled direct calls can bypass the detour")
        );
        Assert.That(
            DetourRuntime.GetInliningWarning(typeof(DetourRuntimeTests).GetMethod(nameof(Target), flags)!),
            Is.Empty
        );
    }

    [Test]
    public void MonoAssemblyAccess_EnablesPrivateMemberBindingForGeneratedAssembly()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new("ConduitDetourAccessTest_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run
        );
        var module = assembly.DefineDynamicModule("main");
        var type = module.DefineType("AccessTest", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod(
            "Read",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            Type.EmptyTypes
        );
        method.GetILGenerator().Emit(
            OpCodes.Call,
            typeof(DetourAccessProbe).GetProperty("Value", BindingFlags.Static | BindingFlags.NonPublic)!.GetMethod
        );
        method.GetILGenerator().Emit(OpCodes.Ret);
        var generated = type.CreateType();

        MonoAssemblyAccess.EnablePrivateAccess(assembly);
        Assert.That(generated.GetMethod("Read")!.Invoke(null, null), Is.EqualTo(DetourAccessProbe.ExpectedValue));
    }

    [Test]
    public void DetourRuntime_LoadsTestsAppliesAndRestoresGeneratedAssembly()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var target = typeof(DetourRuntimeTests).GetMethod(nameof(Target), flags)!;
        var assemblyName = new AssemblyName("ConduitDetourRuntimeTest_" + Guid.NewGuid().ToString("N"));
        var fileName = assemblyName.Name + ".dll";
        var directory = Path.Combine(Path.GetTempPath(), assemblyName.Name);
        Directory.CreateDirectory(directory);

#pragma warning disable 618
        var assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(
            assemblyName,
            AssemblyBuilderAccess.RunAndSave,
            directory
        );
#pragma warning restore 618
        var module = assembly.DefineDynamicModule("main", fileName);
        var generatedTypeName = "ConduitGenerated.Detours.RuntimeTest";
        var type = module.DefineType(
            generatedTypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed
        );
        var replacement = type.DefineMethod(
            "Replace",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            new[] { typeof(int) }
        );
        replacement.SetImplementationFlags(MethodImplAttributes.NoInlining);
        replacement.GetILGenerator().Emit(OpCodes.Ldarg_0);
        replacement.GetILGenerator().Emit(OpCodes.Ldc_I4, 200);
        replacement.GetILGenerator().Emit(OpCodes.Add);
        replacement.GetILGenerator().Emit(OpCodes.Ret);
        var accessProbe = type.DefineMethod(
            "AccessProbe",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(int),
            Type.EmptyTypes
        );
        accessProbe.GetILGenerator().Emit(
            OpCodes.Call,
            typeof(DetourAccessProbe).GetProperty("Value", BindingFlags.Static | BindingFlags.NonPublic)!.GetMethod
        );
        accessProbe.GetILGenerator().Emit(OpCodes.Ret);
        type.CreateType();
        assembly.Save(fileName);

        var image = File.ReadAllBytes(Path.Combine(directory, fileName));
        var mvid = target.Module.ModuleVersionId.ToString("N");
        var token = target.MetadataToken.ToString(CultureInfo.InvariantCulture);

        string Execute(string mode) => DetourRuntime.Execute(
            mode,
            mvid,
            token,
            "runtime-test",
            nameof(DetourRuntimeTests) + "." + nameof(Target),
            "public static int Replace(int arg0)",
            image,
            null,
            generatedTypeName,
            fileName
        );

        try
        {
            var diagnostic = Execute("test");
            Assert.That(diagnostic, Does.Contain("Detourable: yes"));
            Assert.That(DetourRuntime.ActiveCount, Is.Zero);
            Assert.That(Target(1), Is.EqualTo(2));

            var applied = Execute("apply");
            Assert.That(applied, Does.StartWith("Detoured "));
            Assert.That(DetourRuntime.ActiveCount, Is.EqualTo(1));
            Assert.That(DetourRuntime.ActiveMethodNames.Single(), Does.EndWith("." + nameof(Target)));
            Assert.That(Target(1), Is.EqualTo(201));

            var activeDiagnostic = Execute("test");
            Assert.That(activeDiagnostic, Does.Contain("Active detour: yes"));

            var updated = Execute("apply");
            Assert.That(updated, Does.StartWith("Updated detour "));
            Assert.That(Target(1), Is.EqualTo(201));

            var snapshot = DetourRuntime.GetSnapshots().Single();
            Assert.That(DetourRuntime.RestoreAll(), Is.EqualTo(1));
            Assert.That(Target(1), Is.EqualTo(2));
            DetourRuntime.Reapply(snapshot);
            Assert.That(Target(1), Is.EqualTo(201));
        }
        finally
        {
            Execute("restore");
            File.Delete(Path.Combine(directory, fileName));
            Directory.Delete(directory);
        }

        Assert.That(Target(1), Is.EqualTo(2));
        Assert.That(DetourRuntime.ActiveCount, Is.Zero);
        Assert.That(
            Execute("restore"),
            Does.StartWith("No detour is applied")
        );
    }

    [Test]
    public void NativePatch_PreservesReferenceReturnAbi()
    {
        Assert.That(RefTarget(), Is.EqualTo(11));
        WithPatch(nameof(RefTarget), nameof(RefReplacement), () =>
        {
            ref var value = ref RefTarget();
            value = 31;
            Assert.That(replacementStorage, Is.EqualTo(31));
        });
        Assert.That(RefTarget(), Is.EqualTo(11));

        WithPatch(nameof(RefReadonlyTarget), nameof(RefReadonlyReplacement), () =>
        {
            ref readonly var value = ref RefReadonlyTarget();
            Assert.That(value, Is.EqualTo(31));
        });
    }

    [Test]
    public void NativePatch_PreservesSpanAbi()
    {
        var values = new[] { 3, 7 };
        Assert.That(SpanTarget(values), Is.EqualTo(3));
        WithPatch(nameof(SpanTarget), nameof(SpanReplacement), () =>
        {
            Assert.That(SpanTarget(values), Is.EqualTo(7));
            Assert.That(SpanReturnTarget(values)[0], Is.EqualTo(3));
        });
        WithPatch(nameof(SpanReturnTarget), nameof(SpanReturnReplacement), () =>
            Assert.That(SpanReturnTarget(values)[0], Is.EqualTo(7))
        );
    }

    [Test]
    public unsafe void NativePatch_PreservesPointerAndFunctionPointerAbi()
    {
        var value = 4;
        Assert.That(PointerTarget(&value), Is.EqualTo(4));
        WithPatch(nameof(PointerTarget), nameof(PointerReplacement), () =>
        {
            var replacementValue = 4;
            Assert.That(PointerTarget(&replacementValue), Is.EqualTo(8));
        });

        delegate*<int, int> operation = &Increment;
        Assert.That(FunctionPointerTarget(operation, 3), Is.EqualTo(4));
        WithPatch(nameof(FunctionPointerTarget), nameof(FunctionPointerReplacement), () =>
        {
            delegate*<int, int> replacementOperation = &Increment;
            Assert.That(FunctionPointerTarget(replacementOperation, 3), Is.EqualTo(14));
        });
    }

    [Test]
    public void NativePatch_PreservesInstanceReceiverAbi()
    {
        var receiver = new ReferenceReceiver(5);
        Assert.That(receiver.Target(2), Is.EqualTo(7));
        WithPatch(
            typeof(ReferenceReceiver),
            nameof(ReferenceReceiver.Target),
            nameof(ReferenceReceiverReplacement),
            () => Assert.That(receiver.Target(2), Is.EqualTo(12))
        );

        var valueReceiver = new ValueReceiver(5);
        Assert.That(valueReceiver.Target(2), Is.EqualTo(7));
        WithPatch(
            typeof(ValueReceiver),
            nameof(ValueReceiver.Target),
            nameof(ValueReceiverReplacement),
            () => Assert.That(valueReceiver.Target(2), Is.EqualTo(12))
        );
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Target(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Replacement(int value) => value + 100;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref int RefTarget() => ref targetStorage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref int RefReplacement() => ref replacementStorage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref readonly int RefReadonlyTarget() => ref targetStorage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref readonly int RefReadonlyReplacement() => ref replacementStorage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SpanTarget(Span<int> values) => values[0];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SpanReplacement(Span<int> values) => values[1];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Span<int> SpanReturnTarget(Span<int> values) => values[..1];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Span<int> SpanReturnReplacement(Span<int> values) => values[1..];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int PointerTarget(int* value) => *value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int PointerReplacement(int* value) => *value * 2;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int FunctionPointerTarget(delegate*<int, int> operation, int value) => operation(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int FunctionPointerReplacement(delegate*<int, int> operation, int value) => operation(value) + 10;

    static int Increment(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ReferenceReceiverReplacement(ReferenceReceiver @this, int value) => @this.Offset + value + 5;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ValueReceiverReplacement(ref ValueReceiver @this, int value) => @this.Offset + value + 5;

    static void WithPatch(string targetName, string replacementName, Action assertion)
        => WithPatch(typeof(DetourRuntimeTests), targetName, replacementName, assertion);

    static void WithPatch(Type targetType, string targetName, string replacementName, Action assertion)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static
                                                     | System.Reflection.BindingFlags.Instance
                                                     | System.Reflection.BindingFlags.Public
                                                     | System.Reflection.BindingFlags.NonPublic;
        var target = MonoJit.GetCode(targetType.GetMethod(targetName, flags)!);
        var replacement = MonoJit.GetCode(typeof(DetourRuntimeTests).GetMethod(replacementName, flags)!);
        var plan = NativePatch.Plan(target, replacement.Start);
        var original = new PatchPlan(plan.Address, plan.Original, plan.Original, plan.Kind);
        try
        {
            NativePatch.Install(original, plan);
            assertion();
        }
        finally
        {
            if (NativePatch.IsInstalled(plan))
                NativePatch.Restore(plan);
        }
    }

    sealed class ReferenceReceiver
    {
        public ReferenceReceiver(int offset) => Offset = offset;

        public int Offset { get; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Target(int value) => Offset + value;
    }

    struct ValueReceiver
    {
        public ValueReceiver(int offset) => Offset = offset;

        public int Offset { get; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Target(int value) => Offset + value;
    }
}
