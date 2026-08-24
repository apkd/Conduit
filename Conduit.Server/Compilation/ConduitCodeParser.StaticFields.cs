#nullable enable

namespace Conduit
{
    static partial class ConduitCodeParser
    {
        sealed partial class Parser
        {
            bool TryReadStaticFieldDeclaration(int startIndex, int startLine, out SnippetChunk chunk, out int endIndex)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);
                SkipDeclarationPrefix(ref cursor, ref currentLine, out var sawStatic);

                if (!sawStatic)
                {
                    chunk = default;
                    endIndex = startIndex;
                    return false;
                }

                if (TryReadKeyword(cursor, out var decisiveKeyword, out _)
                    && decisiveKeyword != null
                    && (typeKeywords.Contains(decisiveKeyword) || decisiveKeyword == "namespace" || decisiveKeyword == "using"))
                {
                    chunk = default;
                    endIndex = startIndex;
                    return false;
                }

                var statementCursor = cursor;
                var statementLine = currentLine;
                var parenDepth = 0;
                var bracketDepth = 0;
                var braceDepth = 0;
                var seenAssignment = false;

                while (statementCursor < snippet.Length)
                {
                    if (TrySkipCommentOrString(ref statementCursor, ref statementLine))
                        continue;

                    if (Matches(statementCursor, "=>"))
                    {
                        if (!seenAssignment && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        {
                            chunk = default;
                            endIndex = startIndex;
                            return false;
                        }

                        statementCursor += 2;
                        continue;
                    }

                    var ch = snippet[statementCursor];
                    switch (ch)
                    {
                        case '=':
                            if (!seenAssignment
                                && parenDepth == 0
                                && bracketDepth == 0
                                && braceDepth == 0
                                && !IsEqualityLikeOperator(statementCursor))
                                seenAssignment = true;

                            statementCursor++;
                            break;
                        case '(':
                            if (!seenAssignment && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            {
                                chunk = default;
                                endIndex = startIndex;
                                return false;
                            }

                            parenDepth++;
                            statementCursor++;
                            break;
                        case ')':
                            if (parenDepth > 0)
                                parenDepth--;

                            statementCursor++;
                            break;
                        case '[':
                            bracketDepth++;
                            statementCursor++;
                            break;
                        case ']':
                            if (bracketDepth > 0)
                                bracketDepth--;

                            statementCursor++;
                            break;
                        case '{':
                            if (!seenAssignment && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            {
                                chunk = default;
                                endIndex = startIndex;
                                return false;
                            }

                            braceDepth++;
                            statementCursor++;
                            break;
                        case '}':
                            if (braceDepth > 0)
                                braceDepth--;

                            statementCursor++;
                            break;
                        case ';':
                            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            {
                                endIndex = statementCursor + 1;
                                chunk = new(
                                    snippet.Substring(startIndex, endIndex - startIndex),
                                    startLine
                                );
                                return true;
                            }

                            statementCursor++;
                            break;
                        case '\n':
                            statementLine++;
                            statementCursor++;
                            break;
                        case '\r':
                            statementCursor++;
                            break;
                        default:
                            statementCursor++;
                            break;
                    }
                }

                throw CreateParseException(startLine, "Unterminated top-level static field declaration.");
            }

            int ReadSemicolonTerminatedChunk(int startIndex, int startLine, string description)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                var parenDepth = 0;
                var bracketDepth = 0;
                var braceDepth = 0;

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
                            braceDepth++;
                            cursor++;
                            break;
                        case '}':
                            if (braceDepth > 0)
                                braceDepth--;

                            cursor++;
                            break;
                        case ';':
                            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
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

                throw CreateParseException(startLine, $"Unterminated {description}.");
            }
        }
    }
}

