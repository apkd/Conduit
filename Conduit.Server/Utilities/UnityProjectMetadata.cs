using System.Text.RegularExpressions;

namespace Conduit;

static class UnityProjectMetadata
{
    /// <summary>Reads the Unity editor version from a project's ProjectVersion.txt file.</summary>
    internal static string? TryReadEditorVersion(string projectVersionPath)
    {
        try
        {
            foreach (var line in File.ReadLines(projectVersionPath))
            {
                const string prefix = "m_EditorVersion:";
                var lineSpan = line.AsSpan();
                if (!lineSpan.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var version = lineSpan[prefix.Length..].Trim();
                return version.IsEmpty ? null : version.ToString();
            }
        }
        catch { }

        return null;
    }

    /// <summary>Extracts a Unity project path from an editor command line.</summary>
    internal static string? TryExtractProjectPathFromCommandLine(
        string? commandLine,
        Regex projectPathArgumentPattern
    )
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var match = projectPathArgumentPattern.Match(commandLine);
        if (!match.Success)
            return null;

        var projectPath = match.Groups["path"].Value;
        return string.IsNullOrWhiteSpace(projectPath)
            ? null
            : ProjectPathNormalizer.Normalize(projectPath);
    }
}
