using System.Collections.Concurrent;
using System.Text;

namespace Conduit;

sealed class UnityCompilationDiagnosticsReader
{
    const int CompilationDiagnosticsCachePruneThreshold = 64;

    readonly ConcurrentDictionary<string, CompilationDiagnosticsCacheEntry> compilationDiagnosticsCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal CompilationDiagnosticSummary ReadCompilationDiagnosticsSince(string? logPath, long startOffset) =>
        ReadCompilationDiagnostics(logPath, startOffset);

    internal CompilationDiagnosticSummary ReadLatestCompilationDiagnostics(string? logPath) =>
        ReadCompilationDiagnostics(logPath, startOffset: null);

    CompilationDiagnosticSummary ReadCompilationDiagnostics(string? logPath, long? startOffset)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return CompilationDiagnosticSummary.Empty;
        if (!File.Exists(logPath))
        {
            compilationDiagnosticsCache.TryRemove(logPath, out _);
            return CompilationDiagnosticSummary.Empty;
        }

        try
        {
            long? cacheableLength = null;
            DateTime cacheableLastWriteUtc = default;
            CompilationDiagnosticsCacheEntry? exact = null;
            CompilationDiagnosticsCacheEntry? resume = null;
            if (startOffset is null)
            {
                var fileInfo = new FileInfo(logPath);
                cacheableLength = fileInfo.Length;
                cacheableLastWriteUtc = fileInfo.LastWriteTimeUtc;
                if (compilationDiagnosticsCache.TryGetValue(logPath, out var cached))
                {
                    if (cached.Length == cacheableLength
                        && cached.LastWriteUtc == cacheableLastWriteUtc)
                        exact = cached;
                    else if (cached.Length < cacheableLength.Value)
                        resume = cached;
                }
            }

            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (exact != null)
            {
                if (HasMatchingTail(stream, exact))
                    return exact.Summary;

                stream.Seek(0, SeekOrigin.Begin);
            }
            else if (startOffset is > 0 && startOffset.Value < stream.Length)
                stream.Seek(startOffset.Value, SeekOrigin.Begin);
            else if (resume != null)
            {
                if (CanResumeFrom(stream, resume))
                    stream.Seek(resume.Length, SeekOrigin.Begin);
                else
                {
                    resume = null;
                    stream.Seek(0, SeekOrigin.Begin);
                }
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: stream.Position == 0,
                bufferSize: 1024
            );
            var parser = new CompilationDiagnosticAccumulator(resume);
            try
            {
                parser.Read(reader);
                var summary = parser.CreateSummary();
                if (cacheableLength is { } length)
                    TryCache(logPath, reader, stream, length, cacheableLastWriteUtc, summary, ref parser);

                return summary;
            }
            finally
            {
                parser.Dispose();
            }
        }
        catch
        {
            return CompilationDiagnosticSummary.Empty;
        }
    }

    void TryCache(
        string logPath,
        StreamReader reader,
        FileStream stream,
        long length,
        DateTime lastWriteUtc,
        CompilationDiagnosticSummary summary,
        ref CompilationDiagnosticAccumulator parser)
    {
        var fileInfo = new FileInfo(logPath);
        if (fileInfo.Length != length || fileInfo.LastWriteTimeUtc != lastWriteUtc)
            return;

        reader.DiscardBufferedData();
        var tail = ReadTail(stream, length);
        fileInfo.Refresh();
        if (fileInfo.Length != length || fileInfo.LastWriteTimeUtc != lastWriteUtc)
            return;

        compilationDiagnosticsCache[logPath] = parser.CreateCache(length, lastWriteUtc, summary, tail);
        PruneMissingCompilationDiagnostics();
    }

    void PruneMissingCompilationDiagnostics()
    {
        if (compilationDiagnosticsCache.Count <= CompilationDiagnosticsCachePruneThreshold)
            return;

        foreach (var path in compilationDiagnosticsCache.Keys)
            if (!File.Exists(path))
                compilationDiagnosticsCache.TryRemove(path, out _);
    }

    // a trailing fingerprint proves that a larger log is an append rather than a replacement.
    static bool CanResumeFrom(FileStream stream, CompilationDiagnosticsCacheEntry cached)
    {
        if (!cached.CanResume || cached.Length > stream.Length)
            return false;

        if (cached.Tail.Length == 0)
            return cached.Length == 0;

        stream.Seek(cached.Length - cached.Tail.Length, SeekOrigin.Begin);
        Span<byte> tail = stackalloc byte[cached.Tail.Length];
        stream.ReadExactly(tail);
        return tail.SequenceEqual(cached.Tail);
    }

    // metadata timestamps can repeat for same-length rewrites on coarse filesystems.
    static bool HasMatchingTail(FileStream stream, CompilationDiagnosticsCacheEntry cached)
    {
        if (cached.Length != stream.Length)
            return false;
        if (cached.Tail.Length == 0)
            return cached.Length == 0;

        stream.Seek(cached.Length - cached.Tail.Length, SeekOrigin.Begin);
        Span<byte> tail = stackalloc byte[cached.Tail.Length];
        stream.ReadExactly(tail);
        return tail.SequenceEqual(cached.Tail);
    }

    static byte[] ReadTail(FileStream stream, long length)
    {
        const int maximumLength = 256;
        var tail = new byte[(int)Math.Min(maximumLength, length)];
        if (tail.Length == 0)
            return tail;

        stream.Seek(length - tail.Length, SeekOrigin.Begin);
        stream.ReadExactly(tail);
        return tail;
    }
}
