#nullable enable

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Conduit;
using NUnit.Framework;

public sealed partial class DetourRuntimeTests
{
    [Test]
    public void MethodDetour_RestoresOnlyItsOwnRegistration()
    {
        var original = Target(7);
        var count = DetourRuntime.ActiveCount;
        using var first = new MethodDetour(Method(nameof(Target)), Method(nameof(Replacement)));
        Assert.That(Target(7), Is.EqualTo(Replacement(7)));
        Assert.That(DetourRuntime.ActiveCount, Is.EqualTo(count + 1));
        Assert.That(DetourRuntime.ActiveMethodNames, Does.Contain(typeof(DetourRuntimeTests).FullName + "." + nameof(Target)));
        first.Dispose();
        first.Dispose();
        Assert.That(Target(7), Is.EqualTo(original));

        using var second = new MethodDetour(Method(nameof(Target)), Method(nameof(Replacement)));
        first.Dispose();
        Assert.That(Target(7), Is.EqualTo(Replacement(7)));
        Assert.That(DetourRuntime.ActiveCount, Is.EqualTo(count + 1));
        second.Dispose();
        Assert.That(DetourRuntime.ActiveCount, Is.EqualTo(count));
    }

    [Test]
    public void MethodDetour_RejectsConflictsAndSurvivesExternalRestoration()
    {
        var original = Target(7);
        var snapshots = DetourRuntime.GetSnapshots().Length;
        using var first = new MethodDetour(Method(nameof(Target)), Method(nameof(Replacement)));
        Assert.Throws<InvalidOperationException>(() => new MethodDetour(Method(nameof(Target)), Method(nameof(Replacement))));
        Assert.That(Target(7), Is.EqualTo(Replacement(7)));
        Assert.That(DetourRuntime.GetSnapshots(), Has.Length.EqualTo(snapshots));

        DetourRuntime.RestoreAll();
        Assert.That(Target(7), Is.EqualTo(original));
        using var second = new MethodDetour(Method(nameof(Target)), Method(nameof(Replacement)));
        first.Dispose();
        Assert.That(Target(7), Is.EqualTo(Replacement(7)));
    }

    [Test]
    public void MethodDetour_ResolvesPrivateMethodsAndOverloadsByName()
    {
        var type = typeof(DetourRuntimeTests);
        var original = OverloadedTarget(7);
        Assert.Throws<AmbiguousMatchException>(() => new MethodDetour(type, nameof(OverloadedTarget), type, nameof(Replacement)));
        using (new MethodDetour(type, nameof(OverloadedTarget), type, nameof(Replacement), new[] { typeof(int) }))
            Assert.That(OverloadedTarget(7), Is.EqualTo(Replacement(7)));
        Assert.That(OverloadedTarget(7), Is.EqualTo(original));

        using (new MethodDetour(type, nameof(OverloadedTarget), type, nameof(ParameterlessReplacement), Type.EmptyTypes))
            Assert.That(OverloadedTarget(), Is.EqualTo(ParameterlessReplacement()));

        using (new MethodDetour(type.FullName + "." + nameof(Target), type.FullName + "." + nameof(Replacement)))
            Assert.That(Target(7), Is.EqualTo(Replacement(7)));

        using (new MethodDetour(
                   typeof(ReferenceReceiver).FullName + "." + nameof(ReferenceReceiver.Target),
                   type.FullName + "." + nameof(ReferenceReceiverReplacement)))
        {
            var receiver = new ReferenceReceiver(4);
            Assert.That(receiver.Target(7), Is.EqualTo(ReferenceReceiverReplacement(receiver, 7)));
        }
    }

    [Test]
    public void MethodDetour_ReportsMissingNamesWithoutChangingTheTarget()
    {
        var type = typeof(DetourRuntimeTests);
        var original = Target(7);
        Assert.Throws<MissingMethodException>(() => new MethodDetour(type, "MissingMethod", type, nameof(Replacement)));
        Assert.Throws<MissingMethodException>(() => new MethodDetour(type, nameof(Target), type, "MissingMethod"));
        Assert.Throws<TypeLoadException>(() => new MethodDetour("MissingType.Target", type.FullName + "." + nameof(Replacement)));
        Assert.Throws<ArgumentException>(() => new MethodDetour("Target", "Replacement"));
        Assert.That(Target(7), Is.EqualTo(original));
    }

    [Test]
    public void MethodDetour_PreservesReferenceAndValueReceivers()
    {
        var type = typeof(DetourRuntimeTests);
        var receiver = new ReferenceReceiver(4);
        var other = new ReferenceReceiver(8);
        var original = receiver.Target(7);
        using (new MethodDetour(typeof(ReferenceReceiver), nameof(ReferenceReceiver.Target), type, nameof(ReferenceReceiverReplacement)))
        {
            Assert.That(receiver.Target(7), Is.EqualTo(ReferenceReceiverReplacement(receiver, 7)));
            Assert.That(other.Target(7), Is.EqualTo(ReferenceReceiverReplacement(other, 7)));
        }
        Assert.That(receiver.Target(7), Is.EqualTo(original));

        var value = new ValueReceiver(4);
        using (new MethodDetour(typeof(ValueReceiver), nameof(ValueReceiver.Target), type, nameof(ValueReceiverReplacement)))
            Assert.That(value.Target(7), Is.EqualTo(ValueReceiverReplacement(ref value, 7)));
        Assert.That(value.Target(7), Is.EqualTo(original));

        IDetourReceiver explicitReceiver = new ExplicitReceiver();
        using (new MethodDetour(typeof(ExplicitReceiver), typeof(IDetourReceiver).FullName!.Replace('+', '.') + ".Invoke", type, nameof(ExplicitReplacement)))
            Assert.That(explicitReceiver.Invoke(7), Is.EqualTo(ExplicitReplacement((ExplicitReceiver)explicitReceiver, 7)));
    }

    [Test]
    public void MethodDetour_PreservesRefOutAndReadonlyParameters()
    {
        var type = typeof(DetourRuntimeTests);
        var expectedValue = 3;
        var input = 7;
        var expectedResult = ByRefReplacement(ref expectedValue, out var expectedOutput, in input);
        using (new MethodDetour(type, nameof(ByRefTarget), type, nameof(ByRefReplacement),
                   new[] { typeof(int).MakeByRefType(), typeof(int).MakeByRefType(), typeof(int).MakeByRefType() }))
        {
            var value = 3;
            Assert.That(ByRefTarget(ref value, out var output, in input), Is.EqualTo(expectedResult));
            Assert.That(value, Is.EqualTo(expectedValue));
            Assert.That(output, Is.EqualTo(expectedOutput));
        }
    }

    [Test]
    public void MethodDetour_RejectsInvalidSignaturesBeforeChangingCode()
    {
        var target = Method(nameof(Target));
        var original = Target(7);
        var count = DetourRuntime.ActiveCount;
        Assert.Throws<ArgumentNullException>(() => new MethodDetour(null!, target));
        Assert.Throws<ArgumentNullException>(() => new MethodDetour(target, (MethodInfo)null!));
        Assert.Throws<ArgumentException>(() => new MethodDetour(target, target));
        Assert.Throws<ArgumentException>(() => new MethodDetour(target, typeof(ReferenceReceiver).GetMethod(nameof(ReferenceReceiver.Target))!));
        Assert.Throws<ArgumentException>(() => new MethodDetour(target, Method(nameof(ParameterlessReplacement))));
        Assert.Throws<ArgumentException>(() => new MethodDetour(target, Method(nameof(WrongReturnReplacement))));
        Assert.Throws<ArgumentException>(() => new MethodDetour(target, Method(nameof(WrongParameterReplacement))));
        Assert.Throws<ArgumentException>(() => new MethodDetour(Method(nameof(ByRefTarget)), Method(nameof(WrongByRefReplacement))));
        Assert.Throws<ArgumentException>(() => new MethodDetour(Method(nameof(RefReadonlyTarget)), Method(nameof(RefReplacement))));
        Assert.Throws<ArgumentException>(() => new MethodDetour(typeof(ValueReceiver).GetMethod(nameof(ValueReceiver.Target))!, Method(nameof(WrongReceiverReplacement))));
        Assert.Throws<NotSupportedException>(() => new MethodDetour(Method(nameof(GenericTarget)).MakeGenericMethod(typeof(int)), Method(nameof(Replacement))));
        Assert.That(Target(7), Is.EqualTo(original));
        Assert.That(DetourRuntime.ActiveCount, Is.EqualTo(count));
    }

    [Test]
    public unsafe void MethodDetour_PreservesAdvancedSignaturesAndRejectsDifferentFunctionPointers()
    {
        var values = new[] { 3, 7 };
        using (new MethodDetour(Method(nameof(SpanTarget)), Method(nameof(SpanReplacement))))
            Assert.That(SpanTarget(values), Is.EqualTo(SpanReplacement(values)));
        using (new MethodDetour(Method(nameof(SpanReturnTarget)), Method(nameof(SpanReturnReplacement))))
            Assert.That(SpanReturnTarget(values)[0], Is.EqualTo(SpanReturnReplacement(values)[0]));
        using (new MethodDetour(Method(nameof(RefTarget)), Method(nameof(RefReplacement))))
            Assert.That(RefTarget(), Is.EqualTo(RefReplacement()));
        using (new MethodDetour(Method(nameof(RefReadonlyTarget)), Method(nameof(RefReadonlyReplacement))))
            Assert.That(RefReadonlyTarget(), Is.EqualTo(RefReadonlyReplacement()));
        using (new MethodDetour(Method(nameof(PointerTarget)), Method(nameof(PointerReplacement))))
        {
            var value = 7;
            Assert.That(PointerTarget(&value), Is.EqualTo(PointerReplacement(&value)));
        }
        delegate*<int, int> operation = &Increment;
        var original = FunctionPointerTarget(operation, 7);
        Assert.Throws<ArgumentException>(() => new MethodDetour(Method(nameof(FunctionPointerTarget)), Method(nameof(WrongFunctionPointerReplacement))));
        Assert.That(FunctionPointerTarget(operation, 7), Is.EqualTo(original));
        using (new MethodDetour(typeof(DetourRuntimeTests), nameof(FunctionPointerTarget), typeof(DetourRuntimeTests), nameof(FunctionPointerReplacement)))
            Assert.That(FunctionPointerTarget(operation, 7), Is.EqualTo(FunctionPointerReplacement(operation, 7)));
        Assert.That(FunctionPointerTarget(operation, 7), Is.EqualTo(original));
    }

    static MethodInfo Method(string name)
        => typeof(DetourRuntimeTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int OverloadedTarget(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int OverloadedTarget() => 3;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ParameterlessReplacement() => 9;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ByRefTarget(ref int value, out int output, in int input)
    {
        value += input;
        output = value;
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ByRefReplacement(ref int value, out int output, in int input)
    {
        value *= input;
        output = value + input;
        return output - 1;
    }

    static long WrongReturnReplacement(int value) => value;
    static int WrongParameterReplacement(long value) => (int)value;
    static int WrongReceiverReplacement(ValueReceiver receiver, int value) => receiver.Offset + value;
    static int WrongByRefReplacement(ref int value, ref int output, in int input) => value + output + input;
    static unsafe int WrongFunctionPointerReplacement(delegate*<long, int> operation, int value) => operation(value);
    static T GenericTarget<T>(T value) => value;

    interface IDetourReceiver
    {
        int Invoke(int value);
    }

    sealed class ExplicitReceiver : IDetourReceiver
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        int IDetourReceiver.Invoke(int value) => value + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int ExplicitReplacement(ExplicitReceiver receiver, int value) => value + 7;
}
