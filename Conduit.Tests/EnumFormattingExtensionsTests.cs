using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class EnumFormattingExtensionsTests
{
    [Test]
    public async Task ToStringNoAllocReturnsStableReferenceForNamedEnumValues()
    {
        var first = SampleEnum.Beta.ToStringNoAlloc();
        var second = SampleEnum.Beta.ToStringNoAlloc();

        await Assert.That(first).IsEqualTo("Beta");
        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task ToStringNoAllocCachesUnnamedEnumValuesAfterFirstLookup()
    {
        var value = (SampleEnum)123;
        var first = value.ToStringNoAlloc();
        var second = value.ToStringNoAlloc();

        await Assert.That(first).IsEqualTo("123");
        await Assert.That(second).IsSameReferenceAs(first);
    }

    enum SampleEnum
    {
        Alpha = 1,
        Beta = 2,
    }
}
