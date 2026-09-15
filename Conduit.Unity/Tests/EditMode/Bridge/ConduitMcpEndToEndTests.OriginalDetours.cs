#nullable enable

#if UNITY_EDITOR
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;

public sealed partial class ConduitMcpEndToEndTests
{
    static Delegate capturedOriginal = null!;

    [Test]
    public async Task Detour_OriginalCallsSurviveUpdatesRestoreAndReplay()
    {
        const string method = "ConduitMcpEndToEndTests.DetourProbe";
        int expected = DetourProbe(3) + DetourProbe(4);
        try
        {
            AssertSuccessful(await CallDetourAsync(method, "return arg0*100;"), "Detoured");
            AssertSuccessful(await CallDetourAsync(method,
                "global::ConduitMcpEndToEndTests.capturedOriginal=@base;return @base(arg0)+@base(arg0+1);"), "Updated");
            Assert.That(DetourProbe(3), Is.EqualTo(expected));
            var snapshot = DetourRuntime.GetSnapshots().Single();
            var original = capturedOriginal;

            AssertSuccessful(await CallDetourAsync(method, "return arg0<0 ? 0 : @base(arg0)*3;"), "Updated");
            Assert.That(DetourProbe(-1), Is.Zero);
            Assert.That(DetourProbe(3), Is.EqualTo((int)original.DynamicInvoke(3)! * 3));
            AssertSuccessful(await CallDetourAsync(method, "restore"), "Restored");
            Assert.That(original.DynamicInvoke(3), Is.EqualTo(DetourProbe(3)));

            DetourRuntime.Reapply(snapshot); // replay loads a fresh host and binds the original again
            Assert.That(DetourProbe(3), Is.EqualTo(expected));
        }
        finally
        {
            await CallDetourAsync(method, "restore");
        }
    }

    [Test]
    public async Task Detour_OriginalCallsSupportReferenceAndValueReceivers()
    {
        const string referenceMethod = "ConduitMcpEndToEndTests.DetourReceiver.Add";
        const string valueMethod = "ConduitMcpEndToEndTests.OriginalMcpValue.Add";
        var receiver = new DetourReceiver(5);
        int expected = receiver.Invoke(3);
        try
        {
            AssertSuccessful(await CallDetourAsync(referenceMethod, "return @base(@this,arg0)*2;"), "Detoured");
            Assert.That(receiver.Invoke(3), Is.EqualTo(expected * 2));
            AssertSuccessful(await CallDetourAsync(valueMethod, "return @base(ref @this,arg0)*2;"), "Detoured");
            var value = new OriginalMcpValue();
            Assert.That(value.Add(4), Is.EqualTo(value.Value * 2));
            Assert.That(value.Value, Is.EqualTo(4));
        }
        finally
        {
            await CallDetourAsync(referenceMethod, "restore");
            await CallDetourAsync(valueMethod, "restore");
        }
    }

    [Test]
    public async Task Detour_OriginalCallsSupportReferenceReturnsAndOutArguments()
    {
        const string reference = "ConduitMcpEndToEndTests.DetourRefReadonlyProbe";
        const string byRef = "ConduitMcpEndToEndTests.DetourOutProbe";
        int expectedReference = DetourRefReadonlyProbe();
        bool expected = DetourOutProbe(4, out int expectedNumber, out string expectedText);
        try
        {
            AssertSuccessful(await CallDetourAsync(reference, "return ref @base();"), "Detoured");
            Assert.That(DetourRefReadonlyProbe(), Is.EqualTo(expectedReference));
            AssertSuccessful(await CallDetourAsync(byRef, "return @base(arg0,out arg1,out arg2);"), "Detoured");
            Assert.That(DetourOutProbe(4, out int number, out string text), Is.EqualTo(expected));
            Assert.That(number, Is.EqualTo(expectedNumber));
            Assert.That(text, Is.EqualTo(expectedText));
        }
        finally
        {
            await CallDetourAsync(reference, "restore");
            await CallDetourAsync(byRef, "restore");
        }
    }

    [Test]
    public async Task Detour_OriginalAsyncContinuationCanFinishAfterRestore()
    {
        const string method = "ConduitMcpEndToEndTests.OriginalMcpAsync";
        var completion = new TaskCompletionSource<int>();
        Task<int> result;
        try
        {
            AssertSuccessful(await CallDetourAsync(method, "var f=@base;await arg0;return (await f(arg0))*2;"), "Detoured");
            result = OriginalMcpAsync(completion.Task);
            Assert.That(result.IsCompleted, Is.False);
        }
        finally
        {
            await CallDetourAsync(method, "restore");
        }
        completion.SetResult(4);
        Assert.That(await result, Is.EqualTo(await OriginalMcpAsync(completion.Task) * 2));
    }

    [Test]
    public async Task Detour_UnsupportedOriginalLeavesReplacementActiveAndCloningIsOptional()
    {
        const string method = "ConduitMcpEndToEndTests.OriginalMcpSynchronized";
        int expected = OriginalMcpSynchronized(4);
        try
        {
            AssertSuccessful(await CallDetourAsync(method, "// @base is only text here\nreturn arg0*3;"), "Detoured");
            var rejected = await CallDetourAsync(method, "return @base(arg0);");
            Assert.That(rejected.Text, Does.Contain("synchronized"));
            Assert.That(OriginalMcpSynchronized(4), Is.EqualTo(12));
            var initializer = await CallDetourAsync(method, "static int v=@base(1);return v;");
            Assert.That(initializer.Text, Does.Contain("static initialization"));
            Assert.That(OriginalMcpSynchronized(4), Is.EqualTo(12));
        }
        finally
        {
            await CallDetourAsync(method, "restore");
        }
        Assert.That(OriginalMcpSynchronized(4), Is.EqualTo(expected));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.Synchronized)]
    static int OriginalMcpSynchronized(int value) => value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static async Task<int> OriginalMcpAsync(Task<int> value) => await value + 1;

    struct OriginalMcpValue
    {
        internal int Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal int Add(int increment) => Value += increment;
    }
}
#endif
