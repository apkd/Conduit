#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Conduit;
using NUnit.Framework;

public sealed class DetourPlayModeTests
{
    static int targetStorage = 11;
    static int replacementStorage = 29;

    [Test]
    public void NativePatch_ReplacesAndRestoresPlayerMonoMethodEntry()
    {
        Assert.That(Target(2), Is.EqualTo(3));
        WithPatch(nameof(Target), nameof(Replacement), () =>
            Assert.That(Target(2), Is.EqualTo(102))
        );
        Assert.That(Target(2), Is.EqualTo(3));
    }

    [Test]
    public void NativePatch_PreservesAdvancedPlayerAbi()
    {
        var values = new[] { 3, 7 };
        WithPatch(nameof(SpanTarget), nameof(SpanReplacement), () =>
            Assert.That(SpanTarget(values), Is.EqualTo(7))
        );

        WithPatch(nameof(RefReadonlyTarget), nameof(RefReadonlyReplacement), () =>
        {
            ref readonly var value = ref RefReadonlyTarget();
            Assert.That(value, Is.EqualTo(29));
        });
        Assert.That(RefReadonlyTarget(), Is.EqualTo(11));
    }

    [Test]
    public unsafe void NativePatch_PreservesPointerAndFunctionPointerPlayerAbi()
    {
        var first = 3;
        Assert.That(PointerTarget(&first), Is.EqualTo(3));
        WithPatch(nameof(PointerTarget), nameof(PointerReplacement), () =>
        {
            var value = 7;
            Assert.That(PointerTarget(&value), Is.EqualTo(17));
        });

        delegate*<int, int> operation = &Increment;
        Assert.That(FunctionPointerTarget(operation, 3), Is.EqualTo(4));
        WithPatch(nameof(FunctionPointerTarget), nameof(FunctionPointerReplacement), () =>
        {
            delegate*<int, int> patchedOperation = &Increment;
            Assert.That(FunctionPointerTarget(patchedOperation, 3), Is.EqualTo(14));
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Target(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Replacement(int value) => value + 100;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SpanTarget(Span<int> values) => values[0];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int SpanReplacement(Span<int> values) => values[1];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref readonly int RefReadonlyTarget() => ref targetStorage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref readonly int RefReadonlyReplacement() => ref replacementStorage;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int PointerTarget(int* value) => *value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int PointerReplacement(int* value) => *value + 10;

    static int Increment(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int FunctionPointerTarget(delegate*<int, int> operation, int value)
        => operation(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static unsafe int FunctionPointerReplacement(delegate*<int, int> operation, int value)
        => operation(value) + 10;

    static void WithPatch(string targetName, string replacementName, Action assertion)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var target = MonoJit.GetCode(typeof(DetourPlayModeTests).GetMethod(targetName, flags)!);
        var replacement = MonoJit.GetCode(typeof(DetourPlayModeTests).GetMethod(replacementName, flags)!);
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
}
