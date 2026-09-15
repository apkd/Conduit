#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;

public sealed partial class DetourRuntimeTests
{
    static Func<int, int> originalRecursive = null!;
    static int originalCalls;
    static int finallyCalls;

    [Test]
    public void OriginalClonePreservesBranchesFiltersFinallyAndPrivateGenericCalls()
    {
        var clone = Clone<Func<int, int>>(nameof(OriginalComplex));
        foreach (int input in new[] { -2, -1, 0, 1, 2, 5, 8 })
        {
            finallyCalls = 0;
            int expected = OriginalComplex(input);
            int expectedFinally = finallyCalls;
            finallyCalls = 0;
            Assert.That(clone(input), Is.EqualTo(expected));
            Assert.That(finallyCalls, Is.EqualTo(expectedFinally));
        }
    }

    [Test]
    public void OriginalDelegateSurvivesRestoreAndRecursionEntersReplacement()
    {
        int input = 4;
        int expected = OriginalRecursive(input);
        originalCalls = 0;
        using (MethodDetour.Create(Method(nameof(OriginalRecursive)), Method(nameof(OriginalRecursiveReplacement)), out originalRecursive))
        {
            Assert.That(OriginalRecursive(input), Is.EqualTo(expected));
            Assert.That(originalCalls, Is.EqualTo(input + 1));
        }
        Assert.That(originalRecursive(input), Is.EqualTo(expected));
        Assert.That(OriginalRecursive(input), Is.EqualTo(expected));
        var saved = originalRecursive;
        using (MethodDetour.Create(Method(nameof(OriginalRecursive)), Method(nameof(OriginalRecursiveReplacement)), out originalRecursive))
            Assert.That(originalRecursive.Method, Is.EqualTo(saved.Method));
    }

    [Test]
    public void OriginalClonePreservesAlwaysThrowingFinally()
    {
        var clone = Clone<Action>(nameof(OriginalThrowingFinally));
        originalCalls = 0;
        Assert.Throws<InvalidOperationException>(() => OriginalThrowingFinally());
        int expected = originalCalls;
        originalCalls = 0;
        Assert.Throws<InvalidOperationException>(() => clone());
        Assert.That(originalCalls, Is.EqualTo(expected));
    }

    [Test]
    public void OriginalClonePreservesReceiversByReferenceParametersAndReturnAliases()
    {
        var receiver = new OriginalReceiver();
        var instance = (OriginalInstance)OriginalMethod.CreateDelegate(
            typeof(OriginalReceiver).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!, typeof(OriginalInstance));
        Assert.That(instance(receiver, 3), Is.EqualTo(receiver.Read()));
        var value = new OriginalValue();
        var structure = (OriginalStructure)OriginalMethod.CreateDelegate(
            typeof(OriginalValue).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!, typeof(OriginalStructure));
        Assert.That(structure(ref value, 7), Is.EqualTo(value.Value));

        var byRef = Clone<OriginalByRef>(nameof(OriginalRefOut));
        int increment = 3, argument = 9;
        byRef(ref argument, out int result, in increment);
        Assert.That(result, Is.EqualTo(argument));
        var reference = Clone<OriginalReference>(nameof(OriginalRefReturn));
        reference() = argument;
        Assert.That(OriginalRefReturn(), Is.EqualTo(argument));
        var readOnly = Clone<OriginalReadOnlyReference>(nameof(OriginalReadOnlyRefReturn));
        Assert.That(readOnly(), Is.EqualTo(argument));
    }

    [Test]
    public unsafe void OriginalClonePreservesSpansPointersAndPinnedLocals()
    {
        var span = Clone<OriginalSpan>(nameof(OriginalSpanSlice));
        int[] values = { 3, 5, 7 };
        span(values)[0]++;
        Assert.That(values[1], Is.EqualTo(6));
        var pinned = Clone<Func<int[], int>>(nameof(OriginalPinned));
        Assert.That(pinned(values), Is.EqualTo(OriginalPinned(values)));
        var pointer = Clone<OriginalPointer>(nameof(OriginalPointerRead));
        int value = 4;
        Assert.That(pointer(&value), Is.EqualTo(OriginalPointerRead(&value)));
    }

    [Test]
    public async Task OriginalClonePreservesAsyncStateMachines()
    {
        var clone = Clone<Func<Task<int>, Task<int>>>(nameof(OriginalAsync));
        var completion = new TaskCompletionSource<int>();
        var result = clone(completion.Task);
        Assert.That(result.IsCompleted, Is.False);
        completion.SetResult(12);
        Assert.That(await result, Is.EqualTo(await OriginalAsync(completion.Task)));
    }

    [Test]
    public void OriginalCloneRejectsSynchronizedMethodsWithoutChangingExistingDetours()
    {
        var target = Method(nameof(OriginalSynchronized));
        var replacement = Method(nameof(OriginalConstant));
        using (new MethodDetour(target, replacement))
        {
            Assert.Throws<NotSupportedException>(() => OriginalMethod.CreateDelegate(target, typeof(Func<int, int>)));
            Assert.That(OriginalSynchronized(3), Is.EqualTo(OriginalConstant(3)));
        }
        Assert.That(OriginalSynchronized(3), Is.EqualTo(3));
        Assert.Throws<ArgumentException>(() => Clone<Action<int>>(nameof(OriginalRecursive)));
    }

    static T Clone<T>(string name) where T : Delegate
        => (T)OriginalMethod.CreateDelegate(Method(name), typeof(T));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int OriginalComplex(int value)
    {
        try
        {
            try
            {
                if (value < 0)
                    throw new ArgumentException(nameof(value));
                var values = new List<int>();
                for (int index = 0; index < value; index++)
                    values.Add(index);
                switch (value)
                {
                    case 0: return typeof(OriginalReceiver).Name.Length;
                    case 1: return values[0];
                    case 2: return values.Count;
                    case 5: return values[3];
                    default: return values.Count * value;
                }
            }
            catch (ArgumentException exception) when (OriginalFilter(exception, value))
            {
                return value;
            }
            finally
            {
                finallyCalls++;
            }
        }
        catch (ArgumentException)
        {
            return -value;
        }
        finally
        {
            finallyCalls++;
        }
    }

    static bool OriginalFilter(Exception exception, int value) => exception != null && value == -1;

    static void OriginalThrowingFinally()
    {
        try
        {
            originalCalls++;
        }
        finally
        {
            throw new InvalidOperationException();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int OriginalRecursive(int value) => value == 0 ? 1 : value * OriginalRecursive(value - 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int OriginalRecursiveReplacement(int value)
    {
        originalCalls++;
        return originalRecursive(value);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.Synchronized)]
    static int OriginalSynchronized(int value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int OriginalConstant(int value) => value * 2;

    static void OriginalRefOut(ref int value, out int result, in int increment) => result = value += increment;
    static ref int OriginalRefReturn() => ref originalCalls;
    static ref readonly int OriginalReadOnlyRefReturn() => ref originalCalls;
    static Span<int> OriginalSpanSlice(Span<int> values) => values.Slice(1);
    static unsafe int OriginalPointerRead(int* value) => *value;
    static unsafe int OriginalPinned(int[] values)
    {
        fixed (int* pointer = values)
            return pointer[0] + pointer[values.Length - 1];
    }
    static async Task<int> OriginalAsync(Task<int> value) => await value + 1;

    delegate int OriginalInstance(OriginalReceiver receiver, int value);
    delegate int OriginalStructure(ref OriginalValue receiver, int value);
    delegate void OriginalByRef(ref int value, out int result, in int increment);
    delegate ref int OriginalReference();
    delegate ref readonly int OriginalReadOnlyReference();
    delegate Span<int> OriginalSpan(Span<int> values);
    unsafe delegate int OriginalPointer(int* value);

    sealed class OriginalReceiver
    {
        int value;
        int Add(int increment) => value += increment;
        internal int Read() => value;
    }

    struct OriginalValue
    {
        internal int Value;
        int Add(int increment) => Value += increment;
    }
}
