using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Cysharp.Text;

namespace Conduit;

public static partial class ConduitUtility
{
    const string TargetInvocationDiagnostic = "Exception has been thrown by the target of an invocation.";

    public static string GetPipeName(string? projectPath)
    {
        if (ProjectPathNormalizer.Normalize(projectPath) is not { Length: > 0 } normalizedPath)
            return "unity-conduit-unknown";

        var buffer = new char[normalizedPath.Length];
        var count = 0;
        var previousWasSeparator = false;

        foreach (var character in normalizedPath)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[count++] = char.ToLowerInvariant(character);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
                continue;

            buffer[count++] = '_';
            previousWasSeparator = true;
        }

        var start = 0;
        while (start < count && buffer[start] == '_')
            start++;

        while (count > start && buffer[count - 1] == '_')
            count--;

        return count == start
            ? "unity-conduit-unknown"
            : $"unity-conduit-{new string(buffer, start, count - start)}";
    }

    /// <summary>
    /// Creates a compact bridge-safe request identifier.
    /// </summary>
    public static string CreateRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Materializes the current builder contents and trims trailing line endings.
    /// </summary>
    public static string FinishText(ref Utf16ValueStringBuilder builder) => builder.ToString().TrimEnd();

    /// <summary>
    /// Materializes the current builder contents and trims trailing line endings.
    /// </summary>
    public static string FinishText(StringBuilder builder) => builder.ToString().TrimEnd();

    /// <summary>
    /// Gets a live <see cref="Process"/> instance when the process still exists and is accessible.
    /// </summary>
    public static Process? TryGetProcess(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                return null;
            }

            return process;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the executable path for a process when the platform allows it.
    /// </summary>
    public static string? TryGetProcessPath(Process? process)
    {
        if (process == null)
            return null;

        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the Unity editor version from a project's <c>ProjectVersion.txt</c> file.
    /// </summary>
    public static string? TryReadEditorVersion(string projectVersionPath)
    {
        if (!File.Exists(projectVersionPath))
            return null;

        foreach (var line in File.ReadLines(projectVersionPath))
        {
            const string prefix = "m_EditorVersion:";
            var lineSpan = line.AsSpan();
            if (!lineSpan.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var version = lineSpan[prefix.Length..].Trim();
            return version.IsEmpty ? null : version.ToString();
        }

        return null;
    }

    /// <summary>
    /// Extracts and normalizes a Unity project path from a Unity editor command line.
    /// </summary>
    public static string? TryExtractProjectPathFromCommandLine(string? commandLine, Regex projectPathArgumentPattern)
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

    /// <summary>
    /// Formats a duration into a compact human-readable string using at most two units.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        string? primary = null;
        string? secondary = null;

        AddPart(duration.Days, "day");
        AddPart(duration.Hours, "hour");
        AddPart(duration.Minutes, "minute");
        if (primary is null)
            AddPart(Math.Max(1, duration.Seconds), "second");

        return secondary is null ? primary ?? "0 seconds" : primary + " " + secondary;

        void AddPart(int value, string unit)
        {
            if (value <= 0 || secondary is not null)
                return;

            var part = value == 1 ? $"1 {unit}" : $"{value} {unit}s";
            if (primary is null)
                primary = part;
            else
                secondary = part;
        }
    }

    /// <summary>
    /// Replaces double quotes in user-facing text to keep JSON output compact and readable.
    /// </summary>
    public static string? NormalizeUserFacingText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var needsNormalization = false;
        foreach (var character in value)
        {
            if (character is '"' or '\r')
            {
                needsNormalization = true;
                break;
            }
        }

        if (!needsNormalization)
            return value;

        using var builder = ZString.CreateStringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"':
                    builder.Append('\'');
                    break;
                case '\r':
                    if (index + 1 < value.Length && value[index + 1] == '\n')
                        continue;

                    builder.Append('\n');
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Normalizes optional user-facing text and drops empty values.
    /// </summary>
    public static string? NormalizeOptionalUserFacingText(string? value)
    {
        var normalized = NormalizeUserFacingText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Canonicalizes payload text line endings without rewriting content.
    /// </summary>
    public static string? NormalizePayloadText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.IndexOf('\r') < 0)
            return value;

        using var builder = ZString.CreateStringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\r')
            {
                builder.Append(character);
                continue;
            }

            if (index + 1 < value.Length && value[index + 1] == '\n')
                continue;

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Canonicalizes optional payload text and drops empty values.
    /// </summary>
    public static string? NormalizeOptionalPayloadText(string? value)
    {
        var normalized = NormalizePayloadText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Removes diagnostics that only repeat the exception message.
    /// </summary>
    public static string? NormalizeDiagnostic(string? diagnostic, string? exceptionMessage)
    {
        var normalizedDiagnostic = NormalizeUserFacingText(diagnostic);
        if (string.IsNullOrWhiteSpace(normalizedDiagnostic))
            return null;

        var normalizedExceptionMessage = NormalizeUserFacingText(exceptionMessage);
        if (normalizedDiagnostic == normalizedExceptionMessage)
            return null;

        return normalizedDiagnostic == TargetInvocationDiagnostic
               && !string.IsNullOrWhiteSpace(normalizedExceptionMessage)
            ? null
            : normalizedDiagnostic;
    }

    /// <summary>
    /// Converts raw exception details into the compact wire shape used by the MCP surface.
    /// </summary>
    public static ToolExceptionInfo ToToolExceptionInfo(string? type, string? message, string? stackTrace) =>
        new()
        {
            Type = NormalizeOptionalUserFacingText(type) is { } normalizedType
                ? SimplifyTypeName(normalizedType)
                : null,
            Message = NormalizeOptionalUserFacingText(message),
            StackTrace = SimplifyStackTrace(stackTrace),
        };

    /// <summary>
    /// Converts an exception instance into the compact wire shape used by the MCP surface.
    /// </summary>
    public static ToolExceptionInfo ToToolExceptionInfo(Exception exception) =>
        ToToolExceptionInfo(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace
        );

    /// <summary>
    /// Trims namespaces from exception type names.
    /// </summary>
    public static string SimplifyTypeName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return string.Empty;

        var lastDot = typeName.LastIndexOf('.');
        var lastPlus = typeName.LastIndexOf('+');
        var separatorIndex = Math.Max(lastDot, lastPlus);
        return separatorIndex >= 0 && separatorIndex + 1 < typeName.Length
            ? typeName[(separatorIndex + 1)..]
            : typeName;
    }

    /// <summary>
    /// Removes internal Conduit frames and shortens source locations to file-and-line form.
    /// </summary>
    public static string? SimplifyStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return null;

        try
        {
            using var builder = ZString.CreateStringBuilder();
            using var reader = new StringReader(stackTrace);
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || IsInternalStackTraceFrame(trimmed))
                    continue;

                if (builder.Length > 0)
                    builder.Append('\n');

                builder.Append(SimplifyStackTraceLine(trimmed));
            }

            return builder.Length == 0 ? null : builder.ToString();
        }
        catch
        {
            return NormalizeUserFacingText(stackTrace);
        }
    }

    static bool IsInternalStackTraceFrame(string line) =>
        line.Contains("Conduit.", StringComparison.Ordinal)
        || line.Contains("ConduitGenerated.", StringComparison.Ordinal);

    static string SimplifyStackTraceLine(string line)
    {
        var match = StackTraceFilePatternRegex().Match(line);
        if (match.Success)
        {
            var filePath = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
            var fileName = GetSafeFileName(filePath);
            var lineNumber = match.Groups[2].Value;
            return RemoveMethodParameters(line[..match.Index].TrimEnd()) + $" ({fileName}:{lineNumber})";
        }

        var withoutRuntimeLocation = RuntimeLocationPatternRegex().Replace(line, string.Empty).TrimEnd();
        return RemoveMethodParameters(withoutRuntimeLocation);
    }

    static string GetSafeFileName(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return filePath;

        var lastSeparator = filePath.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return lastSeparator >= 0 && lastSeparator + 1 < filePath.Length
            ? filePath[(lastSeparator + 1)..]
            : filePath;
    }

    static string RemoveMethodParameters(string line)
    {
        var closeParen = line.LastIndexOf(')');
        if (closeParen < 0)
            return line;

        var openParen = line.LastIndexOf('(', closeParen);
        if (openParen < 0)
            return line;

        return line.Remove(openParen, closeParen - openParen + 1);
    }

    [GeneratedRegex(@"\s*\(<[^>]+>:\d+\)\s*$", RegexOptions.Compiled)]
    private static partial Regex RuntimeLocationPatternRegex();

    [GeneratedRegex(@"\s*\[0x[0-9a-fA-F]+\]\s+in\s+(.+?)(?::line\s+|:)(\d+)\s*$", RegexOptions.Compiled)]
    private static partial Regex StackTraceFilePatternRegex();
}
