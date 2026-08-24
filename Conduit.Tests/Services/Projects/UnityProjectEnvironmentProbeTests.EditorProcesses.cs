namespace Conduit;

public sealed partial class UnityProjectEnvironmentProbeTests
{
    [Test]
    public async Task ResolveUnityEditorPathUsesMatchedProcessPathFirst()
    {
        var processPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-from-process"));
        var overridePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-from-env"));
        var existingPaths = new HashSet<string> { processPath, overridePath };

        var resolved = UnityEditorPathResolver.ResolveUnityEditorPath(
            "6000.4.0f1",
            processPath,
            overridePath,
            null,
            userHome: "",
            programFiles: "",
            isWindows: false,
            isLinux: true,
            fileExists: existingPaths.Contains
        );

        await Assert.That(resolved).IsEqualTo(processPath);
    }

    [Test]
    public async Task FindMatchingProjectProcessRecognizesCreateProjectEditorAndIgnoresImportWorker()
    {
        const string projectPath = @"C:\Users\runneradmin\AppData\Local\Temp\conduit-project";
        UnityProjectProcessInfo[] processes =
        [
            new(
                200,
                "/opt/Unity/Editor/Unity",
                $"Unity -adb2 -batchMode -name AssetImportWorkerHW0 -projectPath {projectPath} -parentPid 100"
            ),
            new(
                100,
                "/opt/Unity/Editor/Unity",
                $"/opt/Unity/Editor/Unity -createproject {projectPath} -cloneFromTemplate template.tgz"
            ),
        ];

        var match = UnityEditorProcessProbe.FindMatchingProjectProcess(processes, projectPath);

        await Assert.That(match?.ProcessId).IsEqualTo(100);
    }

    [Test]
    public async Task FindMatchingProjectProcessDoesNotTreatImportWorkerAsEditor()
    {
        var projectPath = CreateProjectPath();
        UnityProjectProcessInfo[] processes =
        [
            new(
                200,
                "/opt/Unity/Editor/Unity",
                $"Unity -adb2 -batchMode -name AssetImportWorkerHW0 -projectPath {projectPath} -parentPid 100"
            ),
        ];

        var match = UnityEditorProcessProbe.FindMatchingProjectProcess(processes, projectPath);

        await Assert.That(match).IsNull();
    }

    [Test]
    public async Task ResolveUnityEditorPathUsesConduitOverrideBeforeUnityOverride()
    {
        var conduitOverride = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-conduit-env"));
        var unityOverride = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-generic-env"));
        var existingPaths = new HashSet<string> { conduitOverride, unityOverride };

        var resolved = UnityEditorPathResolver.ResolveUnityEditorPath(
            "6000.4.0f1",
            processPath: null,
            conduitOverride,
            unityOverride,
            userHome: "",
            programFiles: "",
            isWindows: false,
            isLinux: true,
            fileExists: existingPaths.Contains
        );

        await Assert.That(resolved).IsEqualTo(conduitOverride);
    }

    [Test]
    public async Task ResolveUnityEditorPathFindsLinuxHubInstall()
    {
        const string unityVersion = "6000.4.5f1";
        var userHome = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-home-{Guid.NewGuid():N}"));
        var expectedPath = Path.Combine(userHome, "Unity", "Hub", "Editor", unityVersion, "Editor", "Unity");

        var resolved = UnityEditorPathResolver.ResolveUnityEditorPath(
            unityVersion,
            processPath: null,
            conduitEditorOverride: null,
            unityEditorOverride: null,
            userHome,
            programFiles: "",
            isWindows: false,
            isLinux: true,
            fileExists: path => path == expectedPath
        );

        await Assert.That(resolved).IsEqualTo(expectedPath);
    }

}
