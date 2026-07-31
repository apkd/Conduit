#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class ConduitCodeParser
    {
        static readonly HashSet<string> modifierKeywords = new(StringComparer.Ordinal)
        {
            "public",
            "private",
            "protected",
            "internal",
            "file",
            "static",
            "abstract",
            "sealed",
            "partial",
            "readonly",
            "ref",
            "unsafe",
            "new",
            "volatile",
        };

        static readonly HashSet<string> typeKeywords = new(StringComparer.Ordinal)
        {
            "class",
            "struct",
            "interface",
            "enum",
            "record",
            "delegate",
        };

        public static SnippetParseResult Parse(string snippet)
            => new Parser(snippet).Parse();

        sealed class Parser
        {
            readonly string snippet;
            readonly List<SnippetChunk> usings = new();
            readonly List<SnippetChunk> typeDeclarations = new();
            readonly List<SnippetChunk> staticFields = new();
            int index;
            int line = 1;

            public Parser(string snippet) => this.snippet = snippet;

            public SnippetParseResult Parse()
            {
                ParseUsingPhase();
                ParseDeclarationPhase();
                ValidateBody();

                return new(
                    usings,
                    typeDeclarations,
                    staticFields,
                    new()
                    {
                        Text = snippet[index..],
                        StartLine = line,
                    }
                );
            }

            void ParseUsingPhase()
            {
                while (TryReadUsingDirective(index, line, out var chunk, out var endIndex))
                {
                    usings.Add(chunk);
                    AdvanceTo(endIndex);
                }
            }

            void ParseDeclarationPhase()
            {
                while (true)
                {
                    ThrowIfUnsupportedTopLevelKeyword(index, line);

                    if (TryReadUsingDirective(index, line, out _, out _))
                        throw CreateParseException(line, "Using directives must appear before all other top-level items.");

                    if (TryReadTypeDeclaration(index, line, out var typeChunk, out var typeEndIndex))
                    {
                        typeDeclarations.Add(typeChunk);
                        AdvanceTo(typeEndIndex);
                        continue;
                    }

                    if (TryReadStaticFieldDeclaration(index, line, out var fieldChunk, out var fieldEndIndex))
                    {
                        staticFields.Add(fieldChunk);
                        AdvanceTo(fieldEndIndex);
                        continue;
                    }

                    return;
                }
            }

            void ValidateBody()
            {
                var cursor = index;
                var currentLine = line;
                var parenDepth = 0;
                var bracketDepth = 0;
                var braceDepth = 0;
                var atTopLevelStart = true;

                while (cursor < snippet.Length)
                {
                    if (atTopLevelStart && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        var probeIndex = cursor;
                        var probeLine = currentLine;
                        SkipTrivia(ref probeIndex, ref probeLine);
                        if (probeIndex >= snippet.Length)
                            return;

                        if (snippet[probeIndex] == '#')
                            throw CreateParseException(probeLine, "Preprocessor directives are not supported in execute_code.");

                        if (LooksLikeNamespaceDeclaration(probeIndex, probeLine))
                            throw CreateParseException(probeLine, "Namespace declarations are not supported in execute_code.");

                        if (LooksLikeGlobalUsing(probeIndex, probeLine))
                            throw CreateParseException(probeLine, "Global using directives are not supported in execute_code.");

                        if (LooksLikeExternAlias(probeIndex, probeLine))
                            throw CreateParseException(probeLine, "Extern aliases are not supported in execute_code.");

                        if (TryReadUsingDirective(probeIndex, probeLine, out _, out _))
                            throw CreateParseException(probeLine, "Using directives must appear before the first executable statement.");

                        if (TryReadTypeDeclaration(probeIndex, probeLine, out _, out _))
                            throw CreateParseException(probeLine, "Top-level type declarations must appear before the first executable statement.");

                        if (TryReadStaticFieldDeclaration(probeIndex, probeLine, out _, out _))
                            throw CreateParseException(probeLine, "Top-level static field declarations must appear before the first executable statement.");

                        atTopLevelStart = false;
                    }

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
                            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                                atTopLevelStart = true;

                            break;
                        case ';':
                            cursor++;
                            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                                atTopLevelStart = true;

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
            }

            void ThrowIfUnsupportedTopLevelKeyword(int startIndex, int startLine)
            {
                var cursor = startIndex;
                var currentLine = startLine;
                SkipTrivia(ref cursor, ref currentLine);
                if (cursor >= snippet.Length)
                    return;

                if (snippet[cursor] == '#')
                    throw CreateParseException(currentLine, "Preprocessor directives are not supported in snippets.");

                if (LooksLikeNamespaceDeclaration(cursor, currentLine))
                    throw CreateParseException(currentLine, "Namespace declarations are not supported in snippets.");

                if (LooksLikeGlobalUsing(cursor, currentLine))
                    throw CreateParseException(currentLine, "Global using directives are not supported in snippets.");

                if (LooksLikeExternAlias(cursor, currentLine))
                    throw CreateParseException(currentLine, "Extern aliases are not supported in snippets.");
            }

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
                chunk = new()
                {
                    Text = snippet.Substring(startIndex, endIndex - startIndex),
                    StartLine = startLine,
                };
                return true;
            }

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

                chunk = new()
                {
                    Text = snippet.Substring(startIndex, endIndex - startIndex),
                    StartLine = startLine,
                };
                return true;
            }

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
                                chunk = new()
                                {
                                    Text = snippet.Substring(startIndex, endIndex - startIndex),
                                    StartLine = startLine,
                                };
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

            SnippetParseException CreateParseException(int lineNumber, string message)
                => new(lineNumber, message);
        }
    }

    sealed class SnippetParseResult
    {
        public SnippetParseResult(
            IReadOnlyList<SnippetChunk> usings,
            IReadOnlyList<SnippetChunk> typeDeclarations,
            IReadOnlyList<SnippetChunk> staticFields,
            SnippetChunk body
        )
        {
            Usings = usings;
            TypeDeclarations = typeDeclarations;
            StaticFields = staticFields;
            Body = body;
        }

        public IReadOnlyList<SnippetChunk> Usings { get; }

        public IReadOnlyList<SnippetChunk> TypeDeclarations { get; }

        public IReadOnlyList<SnippetChunk> StaticFields { get; }

        public SnippetChunk Body { get; }
    }

    struct SnippetChunk
    {
        public string Text;
        public int StartLine;
    }

    sealed class SnippetParseException : Exception
    {
        public SnippetParseException(int lineNumber, string message)
            : base($"execute_code({lineNumber}): {message}") { }
    }
}
