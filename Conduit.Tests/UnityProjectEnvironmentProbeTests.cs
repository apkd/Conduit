using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class UnityProjectEnvironmentProbeTests
{
    [Test]
    [Arguments("6000.4.0f1", "-projectPath \"{project}\" -logFile \"{absolute}\"", "{absolute}")]
    [Arguments("6000.4.0f1", "-projectPath \"{project}\" -logFile Logs/Custom.log", "{project}/Logs/Custom.log")]
    [Arguments("6000.4.0f1", null, "{legacy}")]
    [Arguments("6000.5.0f1", null, "{project}/Logs/Editor.log")]
    [Arguments("6000.4.0f1", "-projectPath \"{project}\" -logFile -", null)]
    public async Task ResolveEditorLogPathResolvesConfiguredAndDefaultLocations(
        string unityVersion,
        string? commandLineTemplate,
        string? expectedPathTemplate
    )
    {
        var projectPath = CreateProjectPath();
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        var legacyLogPath = Path.Combine(Path.GetTempPath(), "unity-editor.log");
        var absoluteLogPath = Path.GetFullPath(Path.Combine(projectPath, "CustomLogs", "Editor.log"));

        var resolved = UnityProjectEnvironmentProbe.ResolveEditorLogPath(
            normalizedProjectPath,
            unityVersion,
            ReplaceTokens(commandLineTemplate, projectPath, legacyLogPath, absoluteLogPath),
            legacyLogPath
        );

        await Assert.That(resolved)
            .IsEqualTo(
                ReplaceTokens(expectedPathTemplate, projectPath, legacyLogPath, absoluteLogPath)
            );
    }

    [Test]
    public async Task ResolveUnityEditorPathUsesMatchedProcessPathFirst()
    {
        var processPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-from-process"));
        var overridePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-from-env"));
        var existingPaths = new HashSet<string> { processPath, overridePath };

        var resolved = UnityProjectEnvironmentProbe.ResolveUnityEditorPath(
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
    public async Task ResolveUnityEditorPathUsesConduitOverrideBeforeUnityOverride()
    {
        var conduitOverride = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-conduit-env"));
        var unityOverride = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Unity-generic-env"));
        var existingPaths = new HashSet<string> { conduitOverride, unityOverride };

        var resolved = UnityProjectEnvironmentProbe.ResolveUnityEditorPath(
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

        var resolved = UnityProjectEnvironmentProbe.ResolveUnityEditorPath(
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

    static string CreateProjectPath()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-project-{Guid.NewGuid():N}"));

    static string? ReplaceTokens(string? value, string projectPath, string legacyLogPath, string absoluteLogPath)
    {
        if (value is null)
            return null;

        return value
            .Replace("{project}", projectPath, StringComparison.Ordinal)
            .Replace("{legacy}", legacyLogPath, StringComparison.Ordinal)
            .Replace("{absolute}", absoluteLogPath, StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar);
    }
}
