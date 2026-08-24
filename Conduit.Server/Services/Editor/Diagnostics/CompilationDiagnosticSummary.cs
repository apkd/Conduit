namespace Conduit;

readonly record struct CompilationDiagnosticSummary(
    int ErrorCount,
    int WarningCount,
    string? ErrorText,
    string? WarningText
)
{
    internal static CompilationDiagnosticSummary Empty => default;
    internal bool HasAnyDiagnostics => ErrorCount > 0 || WarningCount > 0;
}
