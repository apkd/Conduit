using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ProjectPipeNameTests
{
    [Test]
    [Arguments("B:/src/UnityProject/", "unity-conduit-mnt_b_src_unityproject")]
    [Arguments(" \"B:\\src\\Unity Project\\\" ", "unity-conduit-mnt_b_src_unity_project")]
    [Arguments("B:/mnt/work/My-Game", "unity-conduit-mnt_work_my_game")]
    public async Task FromProjectPathNormalizesIntoStablePipeName(string projectPath, string expectedPipeName)
        => await Assert.That(ConduitUtility.GetPipeName(projectPath)).IsEqualTo(expectedPipeName);
}
