#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static partial class ConduitCodeParser
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

        sealed partial class Parser
        {
            readonly string snippet;
            readonly List<SnippetChunk> usings = new();
            readonly List<SnippetChunk> typeDeclarations = new();
            readonly List<SnippetChunk> staticFields = new();
            int index;
            int line = 1;

            internal Parser(string snippet) => this.snippet = snippet;

            internal SnippetParseResult Parse()
            {
                ParseUsingPhase();
                ParseDeclarationPhase();
                ValidateBody();

                return new(
                    usings,
                    typeDeclarations,
                    staticFields,
                    new(snippet[index..], line)
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

        }
    }
}
