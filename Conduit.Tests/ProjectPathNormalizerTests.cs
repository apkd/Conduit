using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ProjectPathNormalizerTests
{
    [Test]
    [Arguments(null, "")]
    [Arguments("", "")]
    [Arguments("   ", "")]
    [Arguments("\"   \"", "")]
    [Arguments(" B:/Projects/MyGame/ ", "/mnt/b/Projects/MyGame")]
    [Arguments("\"B:\\Projects\\MyGame\\\\\"", "/mnt/b/Projects/MyGame")]
    [Arguments("'B:/Projects/MyGame/'", "/mnt/b/Projects/MyGame")]
    [Arguments(@"B:\Projects\Nested\", "/mnt/b/Projects/Nested")]
    [Arguments(@"B:\", "/mnt/b")]
    [Arguments(@"B:\mnt\b\src\BurstCanvas\", "/mnt/b/src/BurstCanvas")]
    [Arguments(@"\\wsl.localhost\Ubuntu\mnt\b\src\BurstCanvas\", "/mnt/b/src/BurstCanvas")]
    [Arguments("/mnt/b/src/BurstCanvas/", "/mnt/b/src/BurstCanvas")]
    public async Task NormalizeTrimsAndCanonicalizesObviousPathDifferences(string? rawPath, string expectedPath)
        => await Assert.That(ProjectPathNormalizer.Normalize(rawPath)).IsEqualTo(expectedPath);
}
