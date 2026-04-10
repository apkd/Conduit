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
