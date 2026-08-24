using Cysharp.Text;

namespace Conduit;

ref struct CompilationDiagnosticAccumulator
{
    Utf16ValueStringBuilder errors;
    Utf16ValueStringBuilder warnings;
    readonly HashSet<string> seenErrors;
    readonly HashSet<string> seenWarnings;
    int errorCount;
    int warningCount;
    bool inBlock;
    bool sawTundraBlock;
    bool burstBlockActive;

    internal CompilationDiagnosticAccumulator(CompilationDiagnosticsCacheEntry? resume)
    {
        errors = ZString.CreateStringBuilder();
        warnings = ZString.CreateStringBuilder();
        seenErrors = resume == null
            ? new(StringComparer.Ordinal)
            : new(resume.SeenErrors, StringComparer.Ordinal);
        seenWarnings = resume == null
            ? new(StringComparer.Ordinal)
            : new(resume.SeenWarnings, StringComparer.Ordinal);
        errorCount = resume?.Summary.ErrorCount ?? 0;
        warningCount = resume?.Summary.WarningCount ?? 0;
        inBlock = resume?.InBlock ?? false;
        sawTundraBlock = resume?.SawTundraBlock ?? false;
        burstBlockActive = resume?.BurstBlockActive ?? false;
        AppendCachedText(ref errors, resume?.Summary.ErrorText);
        AppendCachedText(ref warnings, resume?.Summary.WarningText);
    }

    internal void Read(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("*** Tundra build", StringComparison.Ordinal))
            {
                sawTundraBlock = true;
                ResetCurrentBlock();
                continue;
            }

            if (!sawTundraBlock
                && (line.Contains("## Script Compilation Error", StringComparison.Ordinal)
                    || line.Contains("## Script Compilation Warning", StringComparison.Ordinal)))
            {
                ResetCurrentBlock();
                continue;
            }

            if (!inBlock)
                continue;

            if (burstBlockActive)
            {
                if (ShouldCaptureBurstContinuation(line))
                {
                    errors.AppendLine(line);
                    continue;
                }

                burstBlockActive = false;
            }

            if (line.Contains(": error ", StringComparison.Ordinal))
            {
                AppendUniqueDiagnostic(ref errors, seenErrors, line, ref errorCount);
                continue;
            }

            if (line.Contains(": warning ", StringComparison.Ordinal))
            {
                AppendUniqueDiagnostic(ref warnings, seenWarnings, line, ref warningCount);
                continue;
            }

            if (IsBurstCompilationError(line))
                burstBlockActive = AppendUniqueDiagnostic(ref errors, seenErrors, line, ref errorCount);
        }
    }

    internal CompilationDiagnosticSummary CreateSummary() =>
        !inBlock
            ? CompilationDiagnosticSummary.Empty
            : new(
                errorCount,
                warningCount,
                errors.Length == 0 ? null : ConduitText.FinishText(ref errors),
                warnings.Length == 0 ? null : ConduitText.FinishText(ref warnings)
            );

    internal CompilationDiagnosticsCacheEntry CreateCache(
        long length,
        DateTime lastWriteUtc,
        CompilationDiagnosticSummary summary,
        byte[] tail) =>
        new(
            length,
            lastWriteUtc,
            summary,
            inBlock,
            sawTundraBlock,
            burstBlockActive,
            seenErrors,
            seenWarnings,
            tail
        );

    internal void Dispose()
    {
        warnings.Dispose();
        errors.Dispose();
    }

    void ResetCurrentBlock()
    {
        inBlock = true;
        burstBlockActive = false;
        errors.Clear();
        warnings.Clear();
        seenErrors.Clear();
        seenWarnings.Clear();
        errorCount = 0;
        warningCount = 0;
    }

    static void AppendCachedText(ref Utf16ValueStringBuilder builder, string? text)
    {
        if (text is not { Length: > 0 })
            return;

        builder.Append(text);
        builder.Append('\n');
    }

    static bool AppendUniqueDiagnostic(
        ref Utf16ValueStringBuilder builder,
        HashSet<string> seenDiagnostics,
        string line,
        ref int count)
    {
        if (!seenDiagnostics.Add(line))
            return false;

        builder.AppendLine(line);
        count++;
        return true;
    }

    static bool IsBurstCompilationError(string line) =>
        line.Contains(": Burst error BC", StringComparison.Ordinal)
        || line.StartsWith("Burst error BC", StringComparison.Ordinal)
        || line.Contains("InvalidOperationException: Burst failed to compile", StringComparison.Ordinal)
        || line.Contains("BuildFailedException: Burst compiler failed running", StringComparison.Ordinal)
        || line.Contains("Unexpected exception Burst.Compiler.", StringComparison.Ordinal)
        || line.Contains("Burst.Compiler.", StringComparison.Ordinal) && line.Contains("Exception:", StringComparison.Ordinal);

    static bool ShouldCaptureBurstContinuation(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        return line.StartsWith("  at ", StringComparison.Ordinal)
               || line.StartsWith("at Burst.Compiler.", StringComparison.Ordinal)
               || line.StartsWith("Time: -c: line ", StringComparison.Ordinal)
               || line.Contains("linker command line", StringComparison.Ordinal)
               || line.Contains("Burst.Compiler.", StringComparison.Ordinal)
               || line.Contains("This Exception was thrown from a job compiled with Burst", StringComparison.Ordinal)
               || line.StartsWith("(Filename:", StringComparison.Ordinal);
    }
}
