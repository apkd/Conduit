using System.Diagnostics;

namespace Conduit;

static class GtkLaunchEnvironment
{
    internal static void Apply(ProcessStartInfo startInfo)
    {
        var settings = TryReadGtkSettings(startInfo, "gtk-3.0")
                       ?? TryReadGtkSettings(startInfo, "gtk-4.0");
        if (settings is null)
            return;

        LaunchEnvironmentVariables.SetIfMissing(
            startInfo,
            "GTK_THEME",
            ResolveThemeValue(
                startInfo,
                GetGtkSetting(settings, "gtk-theme-name"),
                GetGtkSetting(settings, "gtk-application-prefer-dark-theme")
            )
        );
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XCURSOR_THEME", GetGtkSetting(settings, "gtk-cursor-theme-name"));
        LaunchEnvironmentVariables.SetIfMissing(startInfo, "XCURSOR_SIZE", GetGtkSetting(settings, "gtk-cursor-theme-size"));
    }

    static Dictionary<string, string>? TryReadGtkSettings(ProcessStartInfo startInfo, string versionDirectoryName)
    {
        if (!startInfo.Environment.TryGetValue("XDG_CONFIG_HOME", out var configHomePath)
            || string.IsNullOrWhiteSpace(configHomePath))
            return null;

        var settingsPath = Path.Combine(configHomePath, versionDirectoryName, "settings.ini");
        try
        {
            if (!File.Exists(settingsPath))
                return null;

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var inSettingsSection = false;
            foreach (var rawLine in File.ReadLines(settingsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] is '#' or ';')
                    continue;

                if (line[0] == '[' && line[^1] == ']')
                {
                    inSettingsSection = string.Equals(line, "[Settings]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSettingsSection)
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                    continue;

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                if (key.Length > 0 && value.Length > 0)
                    settings[key] = value;
            }

            return settings.Count > 0 ? settings : null;
        }
        catch
        {
            return null;
        }
    }

    static string? GetGtkSetting(IReadOnlyDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    internal static string? ResolveThemeValue(
        ProcessStartInfo startInfo,
        string? themeName,
        string? preferDark
    )
    {
        if (string.IsNullOrWhiteSpace(themeName))
            return null;

        var theme = themeName.Trim();
        if (theme.Contains(':', StringComparison.Ordinal)
            || !IsTruthyGtkSetting(preferDark)
            || GtkThemeDirectoryExists(startInfo, theme))
            return theme;

        const string darkSuffix = "-dark";
        return theme.EndsWith(darkSuffix, StringComparison.OrdinalIgnoreCase)
            ? theme[..^darkSuffix.Length] + ":dark"
            : theme;
    }

    static bool GtkThemeDirectoryExists(ProcessStartInfo startInfo, string themeName)
    {
        try
        {
            foreach (var themeDirectoryPath in EnumerateGtkThemeDirectoryPaths(startInfo, themeName))
                if (Directory.Exists(themeDirectoryPath))
                    return true;
        }
        catch
        {
        }

        return false;
    }

    static IEnumerable<string> EnumerateGtkThemeDirectoryPaths(ProcessStartInfo startInfo, string themeName)
    {
        if (startInfo.Environment.TryGetValue("HOME", out var homePath)
            && !string.IsNullOrWhiteSpace(homePath))
            yield return Path.Combine(homePath, ".themes", themeName);

        if (startInfo.Environment.TryGetValue("XDG_DATA_HOME", out var dataHomePath)
            && !string.IsNullOrWhiteSpace(dataHomePath))
            yield return Path.Combine(dataHomePath, "themes", themeName);

        if (!startInfo.Environment.TryGetValue("XDG_DATA_DIRS", out var dataDirectoryPaths)
            || string.IsNullOrWhiteSpace(dataDirectoryPaths))
            yield break;

        foreach (var dataDirectoryPath in dataDirectoryPaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(dataDirectoryPath, "themes", themeName);
    }

    static bool IsTruthyGtkSetting(string? value) =>
        value?.Trim() is "1" or "true" or "True" or "TRUE" or "yes" or "Yes" or "YES";
}
