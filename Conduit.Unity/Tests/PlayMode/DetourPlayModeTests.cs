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
    static Func<int, int> original = null!;

    [Test]
    public void MethodDetour_CallsOriginalAndRetainsItAfterDisposal()
    {
        int expected = Target(4);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        using (MethodDetour.Create(typeof(DetourPlayModeTests).GetMethod(nameof(Target), flags)!,
                   typeof(DetourPlayModeTests).GetMethod(nameof(WrapOriginal), flags)!, out original))
            Assert.That(Target(4), Is.EqualTo(expected * 2));
        Assert.That(original(4), Is.EqualTo(expected));
        Assert.That(Target(4), Is.EqualTo(expected));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int WrapOriginal(int value) => original(value) * 2;

    [Test]
    public void MethodDetour_ReplacesAndRestoresPlayerMonoMethodEntry()
    {
        Assert.That(Target(2), Is.EqualTo(3));
        WithPatch(nameof(Target), nameof(Replacement), () =>
            Assert.That(Target(2), Is.EqualTo(102))
        );
        Assert.That(Target(2), Is.EqualTo(3));
    }

    [Test]
    public void MethodDetour_PreservesAdvancedPlayerAbi()
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
    public unsafe void MethodDetour_PreservesPointerAndFunctionPointerPlayerAbi()
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
        using var detour = new MethodDetour(typeof(DetourPlayModeTests), targetName, typeof(DetourPlayModeTests), replacementName);
        assertion();
    }
}
