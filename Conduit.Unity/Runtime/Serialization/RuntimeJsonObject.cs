#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Conduit.Runtime
{
    readonly struct RuntimeJsonObject
    {
        RuntimeJsonObject(string source, IReadOnlyList<RuntimeJsonMember> members)
        {
            Source = source;
            Members = members;
        }

        internal string Source { get; }
        internal IReadOnlyList<RuntimeJsonMember> Members { get; }

        internal static RuntimeJsonObject Parse(string source)
        {
            var parser = new Parser(source);
            var members = parser.ParseObject();
            parser.ExpectEnd();
            return new(source, members);
        }

        internal static string ParseString(string source)
        {
            var parser = new Parser(source);
            var value = parser.ReadString();
            parser.ExpectEnd();
            return value;
        }

        struct Parser
        {
            readonly string source;
            int index;

            internal Parser(string source)
            {
                this.source = source;
                index = 0;
            }

            internal IReadOnlyList<RuntimeJsonMember> ParseObject()
            {
                SkipWhitespace();
                Expect('{');
                var members = new List<RuntimeJsonMember>(8);
                SkipWhitespace();
                if (TryConsume('}'))
                    return members;

                while (true)
                {
                    SkipWhitespace();
                    var name = ReadString();
                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    var valueStart = index;
                    SkipValue();
                    members.Add(new(name, source.Substring(valueStart, index - valueStart)));
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return members;
                    Expect(',');
                }
            }

            internal string ReadString() => ReadString(materialize: true)!;

            string? ReadString(bool materialize)
            {
                SkipWhitespace();
                Expect('"');
                var segmentStart = index;
                StringBuilder? builder = null;
                BridgeStringBuilderPool.StringBuilderHandle pooledBuilder = default;
                try
                {
                    while (index < source.Length)
                    {
                        var character = source[index++];
                        if (character == '"')
                        {
                            if (!materialize)
                                return null;
                            if (builder == null)
                                return source.Substring(segmentStart, index - segmentStart - 1);

                            builder.Append(source, segmentStart, index - segmentStart - 1);
                            return builder.ToString();
                        }
                        if (character != '\\')
                            continue;

                        if (index >= source.Length)
                            throw InvalidJson();
                        if (materialize && builder == null)
                        {
                            pooledBuilder = BridgeStringBuilderPool.Rent(
                                out var rentedBuilder
                            );
                            builder = rentedBuilder;
                        }
                        if (materialize)
                            builder!.Append(source, segmentStart, index - segmentStart - 1);
                        switch (source[index++])
                        {
                            case '"':
                                if (materialize)
                                    builder!.Append('"');
                                break;
                            case '\\':
                                if (materialize)
                                    builder!.Append('\\');
                                break;
                            case '/':
                                if (materialize)
                                    builder!.Append('/');
                                break;
                            case 'b':
                                if (materialize)
                                    builder!.Append('\b');
                                break;
                            case 'f':
                                if (materialize)
                                    builder!.Append('\f');
                                break;
                            case 'n':
                                if (materialize)
                                    builder!.Append('\n');
                                break;
                            case 'r':
                                if (materialize)
                                    builder!.Append('\r');
                                break;
                            case 't':
                                if (materialize)
                                    builder!.Append('\t');
                                break;
                            case 'u':
                                if (index + 4 > source.Length
                                    || !ushort.TryParse(
                                        source.AsSpan(index, 4),
                                        NumberStyles.HexNumber,
                                        CultureInfo.InvariantCulture,
                                        out var codeUnit
                                    ))
                                    throw InvalidJson();

                                if (materialize)
                                    builder!.Append((char)codeUnit);
                                index += 4;
                                break;
                            default:
                                throw InvalidJson();
                        }

                        segmentStart = index;
                    }
                }
                finally
                {
                    pooledBuilder.Dispose();
                }

                throw InvalidJson();
            }

            void SkipString() => ReadString(materialize: false);

            internal void ExpectEnd()
            {
                SkipWhitespace();
                if (index != source.Length)
                    throw InvalidJson();
            }

            void SkipValue()
            {
                SkipWhitespace();
                if (index >= source.Length)
                    throw InvalidJson();

                switch (source[index])
                {
                    case '"':
                        SkipString();
                        return;
                    case '{':
                        SkipObject();
                        return;
                    case '[':
                        SkipArray();
                        return;
                    default:
                        var start = index;
                        while (index < source.Length
                               && !char.IsWhiteSpace(source[index])
                               && source[index] is not (',' or '}' or ']'))
                            index++;
                        if (index == start)
                            throw InvalidJson();

                        var token = source.AsSpan(start, index - start);
                        if (!token.Equals("true".AsSpan(), StringComparison.Ordinal)
                            && !token.Equals("false".AsSpan(), StringComparison.Ordinal)
                            && !token.Equals("null".AsSpan(), StringComparison.Ordinal)
                            && !double.TryParse(
                                token,
                                NumberStyles.Float,
                                CultureInfo.InvariantCulture,
                                out _
                            ))
                            throw InvalidJson();
                        return;
                }
            }

            void SkipObject()
            {
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                    return;

                while (true)
                {
                    SkipString();
                    SkipWhitespace();
                    Expect(':');
                    SkipValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return;
                    Expect(',');
                }
            }

            void SkipArray()
            {
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                    return;

                while (true)
                {
                    SkipValue();
                    SkipWhitespace();
                    if (TryConsume(']'))
                        return;
                    Expect(',');
                }
            }

            void SkipWhitespace()
            {
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                    index++;
            }

            bool TryConsume(char expected)
            {
                if (index >= source.Length || source[index] != expected)
                    return false;

                index++;
                return true;
            }

            void Expect(char expected)
            {
                if (!TryConsume(expected))
                    throw InvalidJson();
            }

            static InvalidOperationException InvalidJson() =>
                new("JSON payload was invalid.");
        }
    }
}
