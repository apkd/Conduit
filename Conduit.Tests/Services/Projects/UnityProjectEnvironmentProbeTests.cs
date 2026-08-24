namespace Conduit;

public sealed partial class UnityProjectEnvironmentProbeTests
{
    static string CreateProjectPath()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"conduit-project-{Guid.NewGuid():N}"));

    static string CreateTempProject()
    {
        var projectPath = CreateProjectPath();
        Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
        Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.4.0f1"
        );
        return projectPath;
    }

    static string CreateTempLog(string content)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "Conduit.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var logPath = Path.Combine(directoryPath, "Editor.log");
        File.WriteAllText(logPath, content);
        return logPath;
    }

    static void DeleteTempLog(string logPath)
    {
        var directoryPath = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
            Directory.Delete(directoryPath, recursive: true);
    }

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

    static string[] Lines(string? text) =>
        text?.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];
}
