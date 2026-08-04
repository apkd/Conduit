namespace Conduit;

public sealed class ProjectPipeNameTests
{
    [Test]
    [Arguments("B:/src/UnityProject/", "unity-conduit-mnt_b_src_unityproject")]
    [Arguments(" \"B:\\src\\Unity Project\\\" ", "unity-conduit-mnt_b_src_unity_project")]
    [Arguments("B:/mnt/work/My-Game", "unity-conduit-mnt_work_my_game")]
    public async Task FromProjectPathNormalizesIntoStablePipeName(string projectPath, string expectedPipeName)
        => await Assert.That(ConduitUtility.GetPipeName(projectPath)).IsEqualTo(expectedPipeName);

    [Test]
    public async Task FromLongProjectPathProducesUnixSocketSafePipeName()
    {
        var projectPath = "/home/developer/projects/" + new string('a', 200);

        var pipeName = ConduitUtility.GetPipeName(projectPath);

        await Assert.That(pipeName.Length).IsLessThanOrEqualTo(64);
        await Assert.That(pipeName).StartsWith("unity-conduit-home_developer_projects");
        await Assert.That(pipeName).Contains("-");
    }
}
