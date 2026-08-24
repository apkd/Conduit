namespace Conduit;

public sealed class EnumStringCacheTests
{
    [Test]
    public async Task GetReturnsStableReferenceForNamedEnumValues()
    {
        var first = EnumStringCache<SampleEnum>.Get(SampleEnum.Beta);
        var second = EnumStringCache<SampleEnum>.Get(SampleEnum.Beta);

        await Assert.That(first).IsEqualTo("Beta");
        await Assert.That(second).IsSameReferenceAs(first);
    }

    [Test]
    public async Task GetCachesUnnamedEnumValuesAfterFirstLookup()
    {
        var value = (SampleEnum)123;
        var first = EnumStringCache<SampleEnum>.Get(value);
        var second = EnumStringCache<SampleEnum>.Get(value);

        await Assert.That(first).IsEqualTo("123");
        await Assert.That(second).IsSameReferenceAs(first);
    }

    enum SampleEnum
    {
        Alpha = 1,
        Beta = 2,
    }
}
