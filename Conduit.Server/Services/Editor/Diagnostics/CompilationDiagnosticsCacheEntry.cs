namespace Conduit;

sealed record CompilationDiagnosticsCacheEntry(
    long Length,
    DateTime LastWriteUtc,
    CompilationDiagnosticSummary Summary,
    bool InBlock,
    bool SawTundraBlock,
    bool BurstBlockActive,
    HashSet<string> SeenErrors,
    HashSet<string> SeenWarnings,
    byte[] Tail)
{
    internal bool CanResume => Length == 0 || Tail.Length > 0 && Tail[^1] == (byte)'\n';
}
