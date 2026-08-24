using Cysharp.Text;

namespace Conduit;

static class ConduitText
{
    const string TargetInvocationDiagnostic = "Exception has been thrown by the target of an invocation.";

    /// <summary>Materializes builder contents after trimming trailing whitespace.</summary>
    internal static string FinishText(ref Utf16ValueStringBuilder builder)
    {
        var text = builder.AsSpan();
        var length = text.Length;
        while (length > 0 && char.IsWhiteSpace(text[length - 1]))
            length--;

        return text[..length].ToString();
    }

    /// <summary>Formats a duration using at most two human-readable units.</summary>
    internal static string FormatDuration(TimeSpan duration)
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
    internal static string? NormalizeUserFacingText(string? value) =>
        NormalizePayloadText(value);

    /// <summary>
    /// Normalizes optional user-facing text and drops empty values.
    /// </summary>
    internal static string? NormalizeOptionalUserFacingText(string? value)
    {
        var normalized = NormalizeUserFacingText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Canonicalizes payload text line endings without rewriting content.
    /// </summary>
    internal static string? NormalizePayloadText(string? value)
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
    internal static string? NormalizeOptionalPayloadText(string? value)
    {
        var normalized = NormalizePayloadText(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Removes diagnostics that only repeat the exception message.
    /// </summary>
    internal static string? NormalizeDiagnostic(string? diagnostic, string? exceptionMessage)
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
    internal static ToolExceptionInfo ToToolExceptionInfo(
        string? type,
        string? message,
        string? stackTrace
    ) =>
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
    internal static ToolExceptionInfo ToToolExceptionInfo(Exception exception) =>
        ToToolExceptionInfo(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace
        );

    /// <summary>
    /// Trims namespaces from exception type names.
    /// </summary>
    internal static string SimplifyTypeName(string typeName) =>
        BridgeExceptionFormatter.SimplifyTypeName(typeName);

    /// <summary>Produces compact logical frames from runtime and compiler-generated stack traces.</summary>
    internal static string? SimplifyStackTrace(string? stackTrace) =>
        NormalizePayloadText(BridgeExceptionFormatter.SimplifyStackTrace(stackTrace));
}
