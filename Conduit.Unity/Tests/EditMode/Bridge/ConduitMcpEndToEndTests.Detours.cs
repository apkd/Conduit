#nullable enable

#if UNITY_EDITOR
using System;
using System.Runtime.CompilerServices;
using System.Threading;
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

    [Test]
    [Order(21)]
    public async Task Detour_TaskReturningMethodCompletesWithoutAnotherRequest()
    {
        const string methodName = "ConduitMcpEndToEndTests.DetourAsyncProbe";
        var request = new AsyncDetourRequest { Bytes = new byte[] { 1, 2, 3 } };
        try
        {
            // the long replacement signature reproduces the FIFO framing failure seen in async service detours
            var applied = await client.CallToolAsync(
                BridgeCommandTypes.Detour,
                Args(
                    ("projectPath", projectPath),
                    ("methodName", methodName),
                    ("replacementBody", "return Task.FromException<byte[]>(new InvalidOperationException());")
                ),
                TimeSpan.FromSeconds(20)
            );
            Assert.That(applied.IsError, Is.False, applied.Text);
            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await DetourAsyncProbe(request, CancellationToken.None)
            );
        }
        finally
        {
            var restored = await CallDetourAsync(methodName, "restore");
            Assert.That(restored.IsError, Is.False, restored.Text);
        }

        Assert.That(await DetourAsyncProbe(request, CancellationToken.None), Is.EqualTo(request.Bytes));
    }

    sealed class AsyncDetourRequest
    {
        internal byte[] Bytes = Array.Empty<byte>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Task<byte[]> DetourAsyncProbe(AsyncDetourRequest request, CancellationToken cancellationToken)
        => Task.FromResult(request.Bytes);

}
#endif
