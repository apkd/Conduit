using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    DateTimeOffset utcNow = utcNow;

    public override DateTimeOffset GetUtcNow()
        => utcNow;

    public void Advance(TimeSpan delta)
        => utcNow += delta;
}
