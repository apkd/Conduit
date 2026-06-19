using System.Text.RegularExpressions;

namespace Conduit;

static partial class UnityWindowTitleClassifier
{
    [GeneratedRegex(
        @"^(?:Importing|Importing \(iteration [^)]+\).*|Reloading Domain|Hold on\.\.\.|Running managed callbacks)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ProgressTitleRegex();

    internal static bool IsSafeModeTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && title.Contains("Safe Mode", StringComparison.OrdinalIgnoreCase);

    internal static bool IsProgressTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title)
        && ProgressTitleRegex().IsMatch(title.Trim());

    internal static bool IsSceneReloadPromptText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("open scene", StringComparison.OrdinalIgnoreCase)
               && text.Contains("reload", StringComparison.OrdinalIgnoreCase)
               && (text.Contains("changed on disk", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("modified externally", StringComparison.OrdinalIgnoreCase));
    }
}
