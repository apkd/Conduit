using System.Text;
using Microsoft.CodeAnalysis;

namespace Conduit;

static class SnippetReturnNormalizer
{
    internal static bool TryNormalizeBareReturns(
        SnippetChunk body,
        IEnumerable<Diagnostic> objectResultDiagnostics,
        IEnumerable<Diagnostic> noResultDiagnostics,
        out SnippetChunk normalizedBody)
    {
        // comparing object- and void-return diagnostics identifies bare returns owned by the
        // generated entry point without rewriting invalid returns inside nested local functions.
        normalizedBody = body;
        var remainingNoResultErrors = GetErrorLocationCounts(noResultDiagnostics, "CS0126");
        var recoveredLocations = new HashSet<(int Line, int Column)>();
        foreach (var diagnostic in objectResultDiagnostics)
        {
            if (!TryGetErrorLocation(diagnostic, "CS0126", out var location))
                continue;

            if (remainingNoResultErrors.TryGetValue(location, out var remainingCount)
                && remainingCount > 0)
            {
                remainingNoResultErrors[location] = remainingCount - 1;
                continue;
            }

            recoveredLocations.Add(location);
        }

        if (recoveredLocations.Count == 0)
            return false;

        var insertionOffsets = new List<int>(recoveredLocations.Count);
        foreach (var location in recoveredLocations)
        {
            if (!TryGetBareReturnInsertionOffset(body, location.Line, location.Column, out var insertionOffset))
                return false;
            insertionOffsets.Add(insertionOffset);
        }

        insertionOffsets.Sort();
        var builder = new StringBuilder(body.Text);
        for (int index = insertionOffsets.Count - 1; index >= 0; --index)
            builder.Insert(insertionOffsets[index], " null");

        normalizedBody = body with { Text = builder.ToString() };
        return true;

        static Dictionary<(int Line, int Column), int> GetErrorLocationCounts(
            IEnumerable<Diagnostic> diagnostics,
            string id)
        {
            var counts = new Dictionary<(int Line, int Column), int>();
            foreach (var diagnostic in diagnostics)
            {
                if (!TryGetErrorLocation(diagnostic, id, out var location))
                    continue;
                counts.TryGetValue(location, out var count);
                counts[location] = count + 1;
            }
            return counts;
        }

        static bool TryGetErrorLocation(
            Diagnostic diagnostic,
            string id,
            out (int Line, int Column) location)
        {
            location = default;
            if (diagnostic.Id != id
                || diagnostic.Severity != DiagnosticSeverity.Error
                || !diagnostic.Location.IsInSource)
                return false;

            var start = diagnostic.Location.GetMappedLineSpan().StartLinePosition;
            location = (start.Line + 1, start.Character + 1);
            return true;
        }
    }

    static bool TryGetBareReturnInsertionOffset(
        SnippetChunk body,
        int targetLine,
        int targetColumn,
        out int insertionOffset)
    {
        insertionOffset = 0;
        if (targetLine < body.StartLine || targetColumn < 1)
            return false;

        int offset = 0;
        int line = body.StartLine;
        int column = 1;
        while (offset < body.Text.Length && (line != targetLine || column != targetColumn))
        {
            if (body.Text[offset++] == '\n')
            {
                line++;
                column = 1;
            }
            else
                column++;
        }

        const string returnKeyword = "return";
        if (line != targetLine
            || column != targetColumn
            || offset + returnKeyword.Length > body.Text.Length
            || !body.Text.AsSpan(offset, returnKeyword.Length).SequenceEqual(returnKeyword)
            || offset > 0 && IsIdentifierPart(body.Text[offset - 1])
            || offset + returnKeyword.Length < body.Text.Length
            && IsIdentifierPart(body.Text[offset + returnKeyword.Length]))
            return false;

        int cursor = offset + returnKeyword.Length;
        while (cursor < body.Text.Length)
        {
            if (char.IsWhiteSpace(body.Text[cursor]))
            {
                cursor++;
                continue;
            }

            if (cursor + 1 < body.Text.Length
                && body.Text[cursor] == '/'
                && body.Text[cursor + 1] == '/')
            {
                cursor += 2;
                while (cursor < body.Text.Length && body.Text[cursor] != '\n')
                    cursor++;
                continue;
            }

            if (cursor + 1 < body.Text.Length
                && body.Text[cursor] == '/'
                && body.Text[cursor + 1] == '*')
            {
                var commentEnd = body.Text.IndexOf("*/", cursor + 2, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;
                cursor = commentEnd + 2;
                continue;
            }

            break;
        }

        if (cursor >= body.Text.Length || body.Text[cursor] != ';')
            return false;

        insertionOffset = offset + returnKeyword.Length;
        return true;
    }

    static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';
}
