using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Cysharp.Text;

namespace Conduit;

public static partial class ConduitUtility
{
    const string TargetInvocationDiagnostic = "Exception has been thrown by the target of an invocation.";
    const string PipeNamePrefix = "unity-conduit-";
    const int PipeNameMaxLength = 64;
    const int PipeNamePrefixLength = 14;
    const int PipeNameLegacySlugMaxLength = PipeNameMaxLength - PipeNamePrefixLength;
    const int PipeNameSlugMaxLength = 32;
    const ulong PipeNameHashOffset = 14695981039346656037UL;
    const ulong PipeNameHashPrime = 1099511628211UL;

    public static string GetPipeName(string? projectPath)
    {
        if (ProjectPathNormalizer.Normalize(projectPath) is not { Length: > 0 } normalizedPath)
            return "unity-conduit-unknown";

        var slug = CreatePipeNameSlug(normalizedPath, PipeNameLegacySlugMaxLength + 1);
        if (slug.Length is > 0 and <= PipeNameLegacySlugMaxLength)
            return PipeNamePrefix + slug;

        if (slug.Length > PipeNameSlugMaxLength)
            slug = TrimTrailingSeparator(slug[..PipeNameSlugMaxLength]);

        var hash = CreatePipeNameHash(normalizedPath);
        return slug.Length == 0
            ? PipeNamePrefix + hash
            : $"{PipeNamePrefix}{slug}-{hash}";
    }

    static string CreatePipeNameSlug(string normalizedPath, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(normalizedPath.Length, maxLength));
        var previousWasSeparator = false;

        foreach (var character in normalizedPath)
        {
            if (builder.Length >= maxLength)
                break;

            if (IsAsciiLetterOrDigit(character))
            {
                builder.Append(ToLowerAscii(character));
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator || builder.Length == 0)
                continue;

            builder.Append('_');
            previousWasSeparator = true;
        }

        if (builder.Length > 0 && builder[^1] == '_')
            builder.Length--;

        return builder.ToString();
    }

    static string CreatePipeNameHash(string normalizedPath)
    {
        var hash = PipeNameHashOffset;

        foreach (var character in normalizedPath)
        {
            hash ^= ToLowerAscii(character);
            hash *= PipeNameHashPrime;
        }

        return hash.ToString("x16");
    }

    static string TrimTrailingSeparator(string value) =>
        value.Length > 0 && value[^1] == '_'
            ? value[..^1]
            : value;

    static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z'
        || character is >= 'A' and <= 'Z'
        || character is >= '0' and <= '9';

    static char ToLowerAscii(char character) =>
        character is >= 'A' and <= 'Z'
            ? (char)(character + ('a' - 'A'))
            : character;

    /// <summary>
    /// Creates a compact bridge-safe request identifier.
    /// </summary>
    public static string CreateRequestId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Materializes the current builder contents after trimming trailing whitespace.
    /// </summary>
    public static string FinishText(ref Utf16ValueStringBuilder builder)
    {
        var text = builder.AsSpan();
        var length = text.Length;
        while (length > 0 && char.IsWhiteSpace(text[length - 1]))
            length--;

        return text[..length].ToString();
    }

    /// <summary>
    /// Materializes the current builder contents after trimming trailing whitespace.
    /// </summary>
    public static string FinishText(StringBuilder builder)
    {
        while (builder.Length > 0 && char.IsWhiteSpace(builder[^1]))
            builder.Length--;

        return builder.ToString();
    }

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
    /// Canonicalizes user-facing text line endings without rewriting content.
    /// </summary>
    public static string? NormalizeUserFacingText(string? value) =>
        NormalizePayloadText(value);

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

    /// <summary>Produces compact logical frames from runtime and compiler-generated stack traces.</summary>
    public static string? SimplifyStackTrace(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
            return null;

        try
        {
            using var builder = ZString.CreateStringBuilder();
            using var reader = new StringReader(stackTrace);
            string? pendingSourceWrapper = null;
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0
                    || IsInternalStackTraceFrame(trimmed)
                    || IsAsyncMethodBuilderFrame(trimmed))
                    continue;

                var frame = SimplifyStackTraceLine(trimmed);

                // before the first suspension, roslyn reports both MoveNext and its source wrapper.
                // carry one expected wrapper across ignored builder frames without collapsing recursion.
                if (pendingSourceWrapper is { } sourceWrapper)
                {
                    pendingSourceWrapper = null;
                    if (!frame.IsStateMachine && frame.Identity == sourceWrapper)
                        continue;
                }

                if (builder.Length > 0)
                    builder.Append('\n');

                builder.Append(frame.Text);
                pendingSourceWrapper = frame.IsStateMachine ? frame.Identity : null;
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

    static bool IsAsyncMethodBuilderFrame(string line)
    {
        var frame = line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line;
        return frame.StartsWith("System.Runtime.CompilerServices.AsyncVoidMethodBuilder", StringComparison.Ordinal)
               || frame.StartsWith("System.Runtime.CompilerServices.AsyncTaskMethodBuilder", StringComparison.Ordinal)
               || frame.StartsWith("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder", StringComparison.Ordinal);
    }

    static (string Text, string Identity, bool IsStateMachine) SimplifyStackTraceLine(string line)
    {
        var match = StackTraceFilePatternRegex().Match(line);
        var frame = match.Success
            ? line[..match.Index].TrimEnd()
            : RuntimeLocationPatternRegex().Replace(line, string.Empty).TrimEnd();
        var simplified = SimplifyGeneratedMethodFrame(RemoveMethodParameters(frame));

        if (match.Success)
        {
            var filePath = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar);
            var fileName = GetSafeFileName(filePath);
            var lineNumber = match.Groups[2].Value;
            return ($"{simplified.Text} ({fileName}:{lineNumber})", simplified.Text, simplified.IsStateMachine);
        }

        return (simplified.Text, simplified.Text, simplified.IsStateMachine);
    }

    static (string Text, bool IsStateMachine) SimplifyGeneratedMethodFrame(string frame)
    {
        var stateMachineMatch = StateMachineFramePatternRegex().Match(frame);
        if (stateMachineMatch.Success
            && SimplifyStateMachineMethodName(stateMachineMatch.Groups["method"].Value) is { } methodName)
            return (stateMachineMatch.Groups["type"].Value + ':' + methodName, true);

        var methodSeparator = frame.LastIndexOf(':');
        return methodSeparator >= 0
               && SimplifyLocalFunctionName(frame[(methodSeparator + 1)..]) is { } localFunctionName
            ? (frame[..(methodSeparator + 1)] + localFunctionName, false)
            : (frame, false);
    }

    // only stable roslyn naming shapes are decoded; unfamiliar generated frames retain their diagnostic value.
    static string? SimplifyStateMachineMethodName(string generatedName)
        => SimplifyLocalFunctionName(generatedName)
           ?? (generatedName.Length > 0
               && generatedName.IndexOf('<') < 0
               && generatedName.IndexOf('>') < 0
               ? generatedName
               : null);

    static string? SimplifyLocalFunctionName(string generatedName)
    {
        var match = LocalFunctionNamePatternRegex().Match(generatedName);
        return match.Success
            ? match.Groups["outer"].Value + '.' + match.Groups["local"].Value
            : null;
    }

    static string GetSafeFileName(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return filePath;

        var lastSeparator = filePath.AsSpan().LastIndexOfAny(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
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

        return line.Remove(openParen, closeParen - openParen + 1).TrimEnd();
    }

    [GeneratedRegex(@"^(?<type>.+)/<(?<method>.+)>d(?:__\d+)?:MoveNext$", RegexOptions.Compiled)]
    private static partial Regex StateMachineFramePatternRegex();

    [GeneratedRegex(@"^<(?<outer>[^>]+)>g__(?<local>[^|]+)\|[\d_]+$", RegexOptions.Compiled)]
    private static partial Regex LocalFunctionNamePatternRegex();

    [GeneratedRegex(@"\s*\(<[^>]+>:\d+\)\s*$", RegexOptions.Compiled)]
    private static partial Regex RuntimeLocationPatternRegex();

    [GeneratedRegex(@"\s*\[0x[0-9a-fA-F]+\]\s+in\s+(.+?)(?::line\s+|:)(\d+)\s*$", RegexOptions.Compiled)]
    private static partial Regex StackTraceFilePatternRegex();
}
