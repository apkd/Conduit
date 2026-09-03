#nullable enable

using System;
using System.Runtime.InteropServices;

interface ConduitReflectInterfaceFixture
{
    void ReflectInterfaceMethod();
}

class ConduitReflectBaseFixture
{
    protected int baseProtectedField;

    protected string ReflectBaseOnlyMethod() => string.Empty;

    public virtual string ReflectVirtualMethod() => string.Empty;
}

sealed class ConduitReflectDerivedFixture : ConduitReflectBaseFixture, ConduitReflectInterfaceFixture
{
    int derivedPrivateField;

    public string DerivedProperty { get; private set; } = string.Empty;

    public ConduitReflectDerivedFixture() { }

    static ConduitReflectDerivedFixture() { }

    public T GenericMethod<T>(ref int value, out string text, params T[] items)
    {
        value += items.Length;
        text = string.Empty;
        return items.Length == 0 ? default! : items[0];
    }

    public override string ReflectVirtualMethod() => DerivedProperty;

    public void ReflectInterfaceMethod() { }
}

struct ConduitReflectStructFixture
{
    public int Value;
}

readonly ref struct ConduitReflectRefStructFixture
{
}

enum ConduitReflectEnumFixture
{
    First,
    Second,
}

delegate void ConduitReflectDelegateFixture();

sealed class ConduitReflectSignatureFixture
{
    static int storage;
    static unsafe delegate*<int, int> operation;

    static ref int RefReturn() => ref storage;

    static ref readonly int RefReadonlyReturn() => ref storage;

    static ref int RefProperty => ref storage;

    static ref readonly int RefReadonlyProperty => ref storage;

    static unsafe int* PointerProperty => null;

    static void ReferenceParameters(in int input, ref int reference, out int output)
        => output = input + reference;

    static unsafe Span<int> SpanAndPointers(
        Span<int> values,
        int* pointer,
        delegate*<int, int> managed,
        delegate* unmanaged[Cdecl]<int, int> native)
        => values;

    static @class @event(@class @this) => @this;

    static T Generic<T>() => default!;

    [DllImport("ConduitReflectMissingNativeLibrary")]
    static extern void Native();

    sealed class @class { }
}

sealed class ConduitReflectManyExternFixture
{
    [DllImport("ConduitReflectMissingNativeLibrary")]
    static extern void ConduitExternOne();

    [DllImport("ConduitReflectMissingNativeLibrary")]
    static extern void ConduitExternTwo();

    [DllImport("ConduitReflectMissingNativeLibrary")]
    static extern void ConduitExternThree();

    [DllImport("ConduitReflectMissingNativeLibrary")]
    static extern void ConduitExternFour();

    [DllImport("ConduitReflectMissingNativeLibrary")]
    static extern void ConduitExternFive();

    static T ConduitUnsupported<T>() => default!;
}

sealed class ConduitReflectExactRankFixture
{
    public void ReflectRank() { }
}

sealed class ConduitReflectLooseRankFixture
{
    public void PrefixReflectRankSuffix() { }
}

sealed class ConduitReflectAmbiguousAlpha
{
}

sealed class ConduitReflectAmbiguousBeta
{
}
