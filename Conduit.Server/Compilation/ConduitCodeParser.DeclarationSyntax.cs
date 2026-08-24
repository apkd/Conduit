#nullable enable

namespace Conduit
{
    static partial class ConduitCodeParser
    {
        sealed partial class Parser
        {
            void SkipDeclarationPrefix(ref int cursor, ref int currentLine, out bool sawStatic)
            {
                SkipTrivia(ref cursor, ref currentLine);
                while (TrySkipAttributeSection(ref cursor, ref currentLine))
                    SkipTrivia(ref cursor, ref currentLine);

                sawStatic = false;
                while (TryReadKeyword(cursor, out var keyword, out var keywordEnd) && keyword != null && modifierKeywords.Contains(keyword))
                {
                    if (keyword == "static")
                        sawStatic = true;

                    cursor = keywordEnd;
                    SkipTrivia(ref cursor, ref currentLine);
                }
            }

            bool TrySkipAttributeSection(ref int cursor, ref int currentLine)
            {
                if (cursor >= snippet.Length || snippet[cursor] != '[')
                    return false;

                SkipBalancedDelimiter('[', ']', ref cursor, ref currentLine);
                return true;
            }

            void SkipBalancedDelimiter(char openChar, char closeChar, ref int cursor, ref int currentLine)
            {
                if (cursor >= snippet.Length || snippet[cursor] != openChar)
                    return;

                var depth = 0;
                while (cursor < snippet.Length)
                {
                    if (TrySkipCommentOrString(ref cursor, ref currentLine))
                        continue;

                    var ch = snippet[cursor];
                    if (ch == openChar)
                    {
                        depth++;
                        cursor++;
                        continue;
                    }

                    if (ch == closeChar)
                    {
                        depth--;
                        cursor++;
                        if (depth == 0)
                            return;

                        continue;
                    }

                    if (ch == '\n')
                        currentLine++;

                    cursor++;
                }

                throw CreateParseException(currentLine, $"Unterminated '{openChar}' block in snippet.");
            }

            void AdvanceTo(int endIndex)
            {
                while (index < endIndex && index < snippet.Length)
                {
                    if (snippet[index] == '\n')
                        line++;

                    index++;
                }
            }

            bool LooksLikeNamespaceDeclaration(int startIndex, int startLine)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);
                return TryReadKeyword(cursor, out var keyword, out _) && keyword == "namespace";
            }

            bool LooksLikeGlobalUsing(int startIndex, int startLine)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);
                if (!TryReadKeyword(cursor, out var keyword, out var keywordEnd) || keyword != "global")
                    return false;

                cursor = keywordEnd;
                SkipTrivia(ref cursor, ref currentLine);
                return TryReadKeyword(cursor, out keyword, out _) && keyword == "using";
            }

            bool LooksLikeExternAlias(int startIndex, int startLine)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);
                if (!TryReadKeyword(cursor, out var keyword, out var keywordEnd) || keyword != "extern")
                    return false;

                cursor = keywordEnd;
                SkipTrivia(ref cursor, ref currentLine);
                return TryReadKeyword(cursor, out keyword, out _) && keyword == "alias";
            }

            bool TryReadKeyword(int startIndex, out string? keyword, out int endIndex)
            {
                keyword = null;
                endIndex = startIndex;
                return TryReadIdentifierToken(startIndex, out keyword, out endIndex);
            }

            bool TryReadIdentifierToken(int startIndex, out string? identifier, out int endIndex)
            {
                identifier = null;
                endIndex = startIndex;
                if (startIndex < 0 || startIndex >= snippet.Length || !IsIdentifierStart(startIndex))
                    return false;

                endIndex = startIndex + 1;
                while (endIndex < snippet.Length && IsIdentifierPart(snippet[endIndex]))
                    endIndex++;

                identifier = snippet.Substring(startIndex, endIndex - startIndex);
                return true;
            }
        }
    }
}

