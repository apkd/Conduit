using Cysharp.Text;

namespace Conduit;

static class ToolResponseFormatter
{
    public static string Format(ToolExecutionResult result)
    {
        var exceptionText = FormatException(result.Exception);
        var content = TryGetSingleContent(result, exceptionText)
                      ?? FormatSections(result, exceptionText);
        if (!string.IsNullOrWhiteSpace(result.DisplayName))
            return FormatDisplayName(result.DisplayName, content);

        return content ?? FormatOutcomeFallback(result.Outcome);
    }

    static string? FormatSections(ToolExecutionResult result, string? exceptionText)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            if (result.Outcome == ToolOutcome.Success)
            {
                AppendSection(ref builder, "DIAGNOSTIC", result.Diagnostic);
                AppendSection(ref builder, "RESULT", result.ReturnValue);
                AppendSection(ref builder, "LOG", result.Logs);
            }
            else
            {
                AppendSection(ref builder, "DIAGNOSTIC", result.Diagnostic);
                AppendSection(ref builder, "EXCEPTION", exceptionText);
                AppendSection(ref builder, "RESULT", result.ReturnValue);
                AppendSection(ref builder, "LOG", result.Logs);
            }

            return builder.Length == 0 ? null : ConduitUtility.FinishText(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string FormatDisplayName(string displayName, string? content)
    {
        var builder = ZString.CreateStringBuilder();
        try
        {
            builder.Append("# ");
            builder.Append(displayName);
            if (!string.IsNullOrEmpty(content))
            {
                builder.Append('\n');
                builder.Append(content);
            }

            return ConduitUtility.FinishText(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string? TryGetSingleContent(ToolExecutionResult result, string? exceptionText)
    {
        var diagnostic = result.Diagnostic;
        var returnValue = result.ReturnValue;
        var logs = result.Logs;

        var populatedCount = 0;
        if (!string.IsNullOrWhiteSpace(diagnostic))
            populatedCount++;

        if (!string.IsNullOrWhiteSpace(exceptionText))
            populatedCount++;

        if (!string.IsNullOrWhiteSpace(returnValue))
            populatedCount++;

        if (!string.IsNullOrWhiteSpace(logs))
            populatedCount++;

        if (populatedCount == 0)
            return null;

        if (populatedCount > 1)
            return null;

        return diagnostic
               ?? exceptionText
               ?? returnValue
               ?? logs;
    }

    static void AppendSection(ref Utf16ValueStringBuilder builder, string title, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        if (builder.Length > 0)
        {
            builder.Append('\n');
            builder.Append('\n');
        }

        builder.Append(title);
        builder.Append(':');
        builder.Append('\n');
        builder.Append(content);
    }

    static string? FormatException(ToolExceptionInfo? exception)
    {
        if (exception is null)
            return null;

        var hasType = !string.IsNullOrWhiteSpace(exception.Type);
        var hasMessage = !string.IsNullOrWhiteSpace(exception.Message);
        var hasStackTrace = !string.IsNullOrWhiteSpace(exception.StackTrace);
        if (!hasType && !hasMessage && !hasStackTrace)
            return null;

        var builder = ZString.CreateStringBuilder();
        try
        {
            var type = exception.Type;
            var message = exception.Message;
            var stackTrace = exception.StackTrace;
            if (hasType && hasMessage)
            {
                builder.Append(type!);
                builder.Append(": ");
                builder.Append(message!);
            }
            else if (hasType)
            {
                builder.Append(type!);
            }
            else if (hasMessage)
            {
                builder.Append(message!);
            }

            if (hasStackTrace)
            {
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                    builder.Append('\n');
                }

                builder.Append("Stack Trace:");
                builder.Append('\n');
                builder.Append(stackTrace!);
            }

            return ConduitUtility.FinishText(ref builder);
        }
        finally
        {
            builder.Dispose();
        }
    }

    static string FormatOutcomeFallback(string outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return string.Empty;

        var builder = ZString.CreateStringBuilder();
        try
        {
            var capitalizeNext = true;
            foreach (var character in outcome)
            {
                if (character == '_')
                {
                    builder.Append(' ');
                    capitalizeNext = true;
                    continue;
                }

                builder.Append(capitalizeNext ? char.ToUpperInvariant(character) : character);
                capitalizeNext = false;
            }

            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }
}
