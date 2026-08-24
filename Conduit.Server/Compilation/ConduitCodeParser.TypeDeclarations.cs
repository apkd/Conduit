#nullable enable

namespace Conduit
{
    static partial class ConduitCodeParser
    {
        sealed partial class Parser
        {
            bool TryReadTypeDeclaration(int startIndex, int startLine, out SnippetChunk chunk, out int endIndex)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);
                SkipDeclarationPrefix(ref cursor, ref currentLine, out _);

                if (!TryReadKeyword(cursor, out var keyword, out var keywordEnd)
                    || keyword == null
                    || !typeKeywords.Contains(keyword))
                {
                    chunk = default;
                    endIndex = startIndex;
                    return false;
                }

                endIndex = keyword switch
                {
                    "delegate" => ReadSemicolonTerminatedChunk(keywordEnd, currentLine, "delegate declaration"),
                    _          => ReadTypeDeclarationEnd(keyword, keywordEnd, currentLine),
                };

                chunk = new(
                    snippet.Substring(startIndex, endIndex - startIndex),
                    startLine
                );
                return true;
            }

            int ReadTypeDeclarationEnd(string keyword, int startIndex, int startLine)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                var parenDepth = 0;
                var bracketDepth = 0;

                while (cursor < snippet.Length)
                {
                    if (TrySkipCommentOrString(ref cursor, ref currentLine))
                        continue;

                    var ch = snippet[cursor];
                    switch (ch)
                    {
                        case '(':
                            parenDepth++;
                            cursor++;
                            break;
                        case ')':
                            if (parenDepth > 0)
                                parenDepth--;

                            cursor++;
                            break;
                        case '[':
                            bracketDepth++;
                            cursor++;
                            break;
                        case ']':
                            if (bracketDepth > 0)
                                bracketDepth--;

                            cursor++;
                            break;
                        case '{':
                            if (parenDepth == 0 && bracketDepth == 0)
                            {
                                SkipBalancedDelimiter('{', '}', ref cursor, ref currentLine);
                                return cursor;
                            }

                            cursor++;
                            break;
                        case ';':
                            if (parenDepth == 0 && bracketDepth == 0 && keyword == "record")
                                return cursor + 1;

                            cursor++;
                            break;
                        case '\n':
                            currentLine++;
                            cursor++;
                            break;
                        case '\r':
                            cursor++;
                            break;
                        default:
                            cursor++;
                            break;
                    }
                }

                throw CreateParseException(startLine, $"Unterminated top-level {keyword} declaration.");
            }
        }
    }
}

