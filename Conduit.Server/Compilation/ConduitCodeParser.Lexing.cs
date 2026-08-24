#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static partial class ConduitCodeParser
    {
        sealed partial class Parser
        {
            bool TrySkipCommentOrString(ref int cursor, ref int currentLine)
            {
                if (cursor >= snippet.Length)
                    return false;

                if (Matches(cursor, "//"))
                {
                    cursor += 2;
                    while (cursor < snippet.Length && snippet[cursor] != '\n')
                        cursor++;

                    return true;
                }

                if (Matches(cursor, "/*"))
                {
                    cursor += 2;
                    while (cursor < snippet.Length)
                    {
                        if (Matches(cursor, "*/"))
                        {
                            cursor += 2;
                            return true;
                        }

                        if (snippet[cursor] == '\n')
                            currentLine++;

                        cursor++;
                    }

                    throw CreateParseException(currentLine, "Unterminated block comment in snippet.");
                }

                if (Matches(cursor, "$@\"") || Matches(cursor, "@$\""))
                {
                    SkipInterpolatedString(true, ref cursor, ref currentLine, 3);
                    return true;
                }

                if (Matches(cursor, "$\""))
                {
                    SkipInterpolatedString(false, ref cursor, ref currentLine, 2);
                    return true;
                }

                if (Matches(cursor, "@\""))
                {
                    SkipVerbatimString(ref cursor, ref currentLine, 2);
                    return true;
                }

                if (snippet[cursor] == '"')
                {
                    SkipRegularString(ref cursor, ref currentLine);
                    return true;
                }

                if (snippet[cursor] == '\'')
                {
                    SkipCharLiteral(ref cursor, ref currentLine);
                    return true;
                }

                return false;
            }

            void SkipRegularString(ref int cursor, ref int currentLine)
            {
                cursor++;
                while (cursor < snippet.Length)
                {
                    if (snippet[cursor] == '\\')
                    {
                        cursor += Math.Min(2, snippet.Length - cursor);
                        continue;
                    }

                    if (snippet[cursor] == '"')
                    {
                        cursor++;
                        return;
                    }

                    if (snippet[cursor] == '\n')
                        currentLine++;

                    cursor++;
                }

                throw CreateParseException(currentLine, "Unterminated string literal in snippet.");
            }

            void SkipVerbatimString(ref int cursor, ref int currentLine, int prefixLength)
            {
                cursor += prefixLength;
                while (cursor < snippet.Length)
                {
                    if (snippet[cursor] == '"')
                    {
                        if (cursor + 1 < snippet.Length && snippet[cursor + 1] == '"')
                        {
                            cursor += 2;
                            continue;
                        }

                        cursor++;
                        return;
                    }

                    if (snippet[cursor] == '\n')
                        currentLine++;

                    cursor++;
                }

                throw CreateParseException(currentLine, "Unterminated verbatim string literal in snippet.");
            }

            void SkipInterpolatedString(bool verbatim, ref int cursor, ref int currentLine, int prefixLength)
            {
                cursor += prefixLength;
                while (cursor < snippet.Length)
                {
                    if (!verbatim && snippet[cursor] == '\\')
                    {
                        cursor += Math.Min(2, snippet.Length - cursor);
                        continue;
                    }

                    if (snippet[cursor] == '"')
                    {
                        if (verbatim && cursor + 1 < snippet.Length && snippet[cursor + 1] == '"')
                        {
                            cursor += 2;
                            continue;
                        }

                        cursor++;
                        return;
                    }

                    if (snippet[cursor] == '{')
                    {
                        if (cursor + 1 < snippet.Length && snippet[cursor + 1] == '{')
                        {
                            cursor += 2;
                            continue;
                        }

                        cursor++;
                        SkipInterpolation(ref cursor, ref currentLine);
                        continue;
                    }

                    if (snippet[cursor] == '}' && cursor + 1 < snippet.Length && snippet[cursor + 1] == '}')
                    {
                        cursor += 2;
                        continue;
                    }

                    if (snippet[cursor] == '\n')
                        currentLine++;

                    cursor++;
                }

                throw CreateParseException(currentLine, "Unterminated interpolated string literal in snippet.");
            }

            void SkipInterpolation(ref int cursor, ref int currentLine)
            {
                var depth = 1;
                while (cursor < snippet.Length)
                {
                    if (TrySkipCommentOrString(ref cursor, ref currentLine))
                        continue;

                    var ch = snippet[cursor];
                    if (ch == '{')
                    {
                        depth++;
                        cursor++;
                        continue;
                    }

                    if (ch == '}')
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

                throw CreateParseException(currentLine, "Unterminated interpolated string hole in snippet.");
            }

            void SkipCharLiteral(ref int cursor, ref int currentLine)
            {
                cursor++;
                while (cursor < snippet.Length)
                {
                    if (snippet[cursor] == '\\')
                    {
                        cursor += Math.Min(2, snippet.Length - cursor);
                        continue;
                    }

                    if (snippet[cursor] == '\'')
                    {
                        cursor++;
                        return;
                    }

                    if (snippet[cursor] == '\n')
                        currentLine++;

                    cursor++;
                }

                throw CreateParseException(currentLine, "Unterminated character literal in snippet.");
            }

            void SkipTrivia(ref int cursor, ref int currentLine)
            {
                while (cursor < snippet.Length)
                {
                    if (Matches(cursor, "//"))
                    {
                        cursor += 2;
                        while (cursor < snippet.Length && snippet[cursor] != '\n')
                            cursor++;

                        continue;
                    }

                    if (Matches(cursor, "/*"))
                    {
                        cursor += 2;
                        while (cursor < snippet.Length)
                        {
                            if (Matches(cursor, "*/"))
                            {
                                cursor += 2;
                                break;
                            }

                            if (snippet[cursor] == '\n')
                                currentLine++;

                            cursor++;
                        }

                        if (cursor > snippet.Length)
                            throw CreateParseException(currentLine, "Unterminated block comment in snippet.");

                        continue;
                    }

                    var ch = snippet[cursor];
                    if (!char.IsWhiteSpace(ch))
                        return;

                    if (ch == '\n')
                        currentLine++;

                    cursor++;
                }
            }

            bool Matches(int startIndex, string value)
            {
                if (startIndex + value.Length > snippet.Length)
                    return false;

                for (var offset = 0; offset < value.Length; offset++)
                    if (snippet[startIndex + offset] != value[offset])
                        return false;

                return true;
            }

            bool IsEqualityLikeOperator(int location)
            {
                var previous = location > 0 ? snippet[location - 1] : '\0';
                var next = location + 1 < snippet.Length ? snippet[location + 1] : '\0';
                return previous == '=' || previous == '!' || previous == '<' || previous == '>' || next == '=' || next == '>';
            }

            bool IsIdentifierStart(int location)
            {
                var ch = snippet[location];
                return char.IsLetter(ch) || ch == '_';
            }

            static bool IsIdentifierPart(char ch)
                => char.IsLetterOrDigit(ch) || ch == '_';

            static SnippetParseException CreateParseException(int lineNumber, string message)
                => new(lineNumber, message);
        }
    }
}
