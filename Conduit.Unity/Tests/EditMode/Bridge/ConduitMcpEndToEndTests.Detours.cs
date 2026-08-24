#nullable enable

#if UNITY_EDITOR
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;

public sealed partial class ConduitMcpEndToEndTests
{
    [Test]
    [Order(18)]
    public async Task Detour_TestsAppliesAndRestoresPrivateStaticMethod()
    {
        const string methodName = "ConduitMcpEndToEndTests.DetourProbe";
        Assert.That(DetourProbe(1), Is.EqualTo(2));

        var tested = await CallDetourAsync(methodName, "test");
        AssertSuccessful(tested, "Detourable: yes", "int Replace(int arg0)", "Active detour: no");

        try
        {
            var applied = await CallDetourAsync(methodName, "return arg0 + 100;");
            AssertSuccessful(applied, "Detoured", methodName);
            Assert.That(DetourProbe(1), Is.EqualTo(101));
        }
        finally
        {
            var restored = await CallDetourAsync(methodName, "restore");
            AssertSuccessful(restored, "Restored the original implementation");
        }

        Assert.That(DetourProbe(1), Is.EqualTo(2));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int DetourProbe(int value) => value + 1;

    [Test]
    [Order(19)]
    public async Task Detour_SupportsInstanceReceiversAndPrivateMemberAccess()
    {
        const string methodName = "ConduitMcpEndToEndTests.DetourReceiver.Add";
        var receiver = new DetourReceiver(5);
        Assert.That(receiver.Invoke(2), Is.EqualTo(7));

        try
        {
            var applied = await CallDetourAsync(
                methodName,
                "return @this.offset + arg0 + 100;"
            );
            AssertSuccessful(applied, "Detoured", methodName);
            Assert.That(receiver.Invoke(2), Is.EqualTo(107));
        }
        finally
        {
            var restored = await CallDetourAsync(methodName, "restore");
            AssertSuccessful(restored, "Restored the original implementation");
        }

        Assert.That(receiver.Invoke(2), Is.EqualTo(7));
    }

    sealed class DetourReceiver
    {
        readonly int offset;

        internal DetourReceiver(int offset) => this.offset = offset;

        internal int Invoke(int value) => Add(value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        int Add(int value) => offset + value;
    }

    [Test]
    [Order(20)]
    public async Task Detour_SupportsSpanAndRefReadonlyReturnSignatures()
    {
        const string spanMethod = "ConduitMcpEndToEndTests.DetourSpanProbe";
        var values = new[] { 3, 7 };
        Assert.That(DetourSpanProbe(values), Is.EqualTo(3));
        try
        {
            var applied = await CallDetourAsync(spanMethod, "return arg0[1];");
            AssertSuccessful(applied, "Detoured", spanMethod);
            Assert.That(DetourSpanProbe(values), Is.EqualTo(7));
        }
        finally
        {
            var restored = await CallDetourAsync(spanMethod, "restore");
            AssertSuccessful(restored, "Restored the original implementation");
        }
        Assert.That(DetourSpanProbe(values), Is.EqualTo(3));

        const string refMethod = "ConduitMcpEndToEndTests.DetourRefReadonlyProbe";
        Assert.That(DetourRefReadonlyProbe(), Is.EqualTo(11));
        Assert.That(detourReplacementStorage, Is.EqualTo(29));
        try
        {
            var applied = await CallDetourAsync(
                refMethod,
                "return ref global::ConduitMcpEndToEndTests.detourReplacementStorage;"
            );
            AssertSuccessful(applied, "Detoured", refMethod);
            Assert.That(DetourRefReadonlyProbe(), Is.EqualTo(29));
        }
        finally
        {
            var restored = await CallDetourAsync(refMethod, "restore");
            AssertSuccessful(restored, "Restored the original implementation");
        }
        Assert.That(DetourRefReadonlyProbe(), Is.EqualTo(11));
    }

    static int detourOriginalStorage = 11;
    static int detourReplacementStorage = 29;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int DetourSpanProbe(Span<int> values) => values[0];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ref readonly int DetourRefReadonlyProbe() => ref detourOriginalStorage;

}
#endif
