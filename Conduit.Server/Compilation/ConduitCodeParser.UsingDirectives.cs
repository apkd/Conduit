#nullable enable

namespace Conduit
{
    static partial class ConduitCodeParser
    {
        sealed partial class Parser
        {
            bool TryReadUsingDirective(int startIndex, int startLine, out SnippetChunk chunk, out int endIndex)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);

                if (!TryReadKeyword(cursor, out var keyword, out var keywordEnd) || keyword != "using")
                {
                    chunk = default;
                    endIndex = startIndex;
                    return false;
                }

                var bodyCursor = keywordEnd;
                SkipTrivia(ref bodyCursor, ref currentLine);
                if (bodyCursor >= snippet.Length)
                    throw CreateParseException(startLine, "Unterminated using directive.");

                if (snippet[bodyCursor] == '(')
                {
                    chunk = default;
                    endIndex = startIndex;
                    return false;
                }

                if (TryReadKeyword(bodyCursor, out var bodyKeyword, out _) && (bodyKeyword == "var" || bodyKeyword == "await"))
                {
                    chunk = default;
                    endIndex = startIndex;
                    return false;
                }

                endIndex = ReadSemicolonTerminatedChunk(cursor, currentLine, "using directive");
                chunk = new(
                    snippet.Substring(startIndex, endIndex - startIndex),
                    startLine
                );
                return true;
            }
        }
    }
}

