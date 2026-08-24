using System.Text.RegularExpressions;

namespace Conduit;

static partial class UnityEditorLogProbe
{
    [GeneratedRegex("-logFile\\s+(?:\"(?<path>[^\"]*)\"|(?<path>\\S+))", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex LogFileArgumentRegex();

    static string LegacyEditorLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Unity",
        "Editor",
        "Editor.log"
    );

    internal static string GetRestartLogPath(string projectPath)
    {
        var normalizedProjectPath = ProjectPathNormalizer.Normalize(projectPath);
        return BuildProjectLogPath(normalizedProjectPath);
    }

    internal static string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot) =>
        ResolveEditorLogPath(snapshot, snapshot.MatchedProcess);

    internal static string? ResolveEditorLogPath(UnityProjectEnvironmentSnapshot snapshot, UnityProjectProcessInfo? processInfo) =>
        ResolveEditorLogPath(snapshot.ProjectPath, snapshot.EditorVersion, processInfo?.CommandLine, LegacyEditorLogPath);

    internal static EditorLogSnapshot GetEditorLogSnapshot(string? logPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
                return default;

            var fileInfo = new FileInfo(logPath);
            DateTimeOffset? lastWriteUtc = fileInfo.LastWriteTimeUtc == default
                ? null
                : new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);

            return new(fileInfo.Length, lastWriteUtc);
        }
        catch
        {
            return default;
        }
    }

    internal static bool UsesProjectRelativeDefaultEditorLog(string? unityVersion)
    {
        if (string.IsNullOrWhiteSpace(unityVersion))
            return false;

        var version = unityVersion.AsSpan();
        var firstDot = version.IndexOf('.');
        if (firstDot < 0)
            return false;

        var remainder = version[(firstDot + 1)..];
        var secondDot = remainder.IndexOf('.');
        var majorSpan = version[..firstDot];
        var minorSpan = secondDot < 0 ? remainder : remainder[..secondDot];
        if (!int.TryParse(majorSpan, out var major))
            return false;

        if (!int.TryParse(minorSpan, out var minor))
            return false;

        return (major, minor) is ( > 6000, _) or (6000, >= 5);
    }

    internal static string? ResolveEditorLogPath(
        string normalizedProjectPath,
        string? editorVersion,
        string? commandLine,
        string legacyEditorLogPath
    )
    {
        if (TryExtractLogFilePathFromCommandLine(commandLine) is { } configuredLogPath)
            return ResolveConfiguredLogPath(normalizedProjectPath, configuredLogPath);

        return UsesProjectRelativeDefaultEditorLog(editorVersion)
            ? BuildProjectLogPath(normalizedProjectPath)
            : legacyEditorLogPath;
    }

    internal static string? TryExtractLogFilePathFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var match = LogFileArgumentRegex().Match(commandLine);
        if (!match.Success)
            return null;

        var logPath = match.Groups["path"].Value;
        return string.IsNullOrWhiteSpace(logPath) ? null : logPath;
    }

    static string BuildProjectLogPath(string normalizedProjectPath)
    {
        if (normalizedProjectPath.Length == 0)
            return string.Empty;

        var platformProjectPath = ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath);
        return Path.GetFullPath(Path.Combine(platformProjectPath, "Logs", "Editor.log"));
    }

    static string? ResolveConfiguredLogPath(string normalizedProjectPath, string configuredLogPath)
    {
        if (string.IsNullOrWhiteSpace(configuredLogPath)
            || configuredLogPath == "-")
            return null;

        if (Path.IsPathRooted(configuredLogPath))
            return Path.GetFullPath(configuredLogPath);

        if (normalizedProjectPath.Length == 0)
            return null;

        return Path.GetFullPath(configuredLogPath, ProjectPathNormalizer.ToPlatformPath(normalizedProjectPath));
    }
}
