#nullable enable

using System;
using System.Text;

namespace Conduit
{
    static class BurstSignatureFormatter
    {
        internal static string FormatRawBurstSignatureParameters(string line)
        {
            if (line.IndexOf('|') < 0)
                return line;

            StringBuilder? builder = null;
            var offset = 0;
            while (offset < line.Length)
            {
                var open = line.IndexOf('(', offset);
                if (open < 0)
                    break;

                var close = FindMatchingParen(line, open);
                if (close < 0)
                    break;

                var parameters = line[(open + 1)..close];
                if (parameters.IndexOf('|') < 0)
                {
                    offset = close + 1;
                    continue;
                }

                if (!IsLikelySignatureOpen(line, open) || !LooksLikeRawBurstParameterList(parameters))
                {
                    offset = close + 1;
                    continue;
                }

                builder ??= new(line.Length);
                builder.Append(line, offset, open - offset + 1);
                AppendRawBurstParameters(builder, parameters);
                builder.Append(')');
                offset = close + 1;
            }

            if (builder == null)
                return line;

            builder.Append(line, offset, line.Length - offset);
            return builder.ToString();
        }

        static bool IsLikelySignatureOpen(string text, int open)
            => open > 0 && IsSignatureNameEnd(text[open - 1]);

        static bool IsSignatureNameEnd(char character)
            => char.IsLetterOrDigit(character)
               || character is '_' or '>' or ']';

        static int FindMatchingParen(string text, int open)
        {
            var depth = 0;
            for (var index = open; index < text.Length; index++)
            {
                if (text[index] == '(')
                    depth++;
                else if (text[index] == ')' && --depth == 0)
                    return index;
            }

            return -1;
        }

        static bool LooksLikeRawBurstParameterList(string parameters)
        {
            var start = 0;
            var depth = 0;
            var count = 0;
            for (var index = 0; index <= parameters.Length; index++)
            {
                if (index < parameters.Length)
                {
                    if (parameters[index] is '<' or '[')
                    {
                        depth++;
                        continue;
                    }

                    if (parameters[index] is '>' or ']')
                    {
                        if (depth > 0)
                            depth--;

                        continue;
                    }

                    if (parameters[index] != '|' || depth != 0)
                        continue;
                }

                if (!LooksLikeRawBurstParameter(parameters[start..index]))
                    return false;

                count++;
                start = index + 1;
            }

            return count > 1;
        }

        static bool LooksLikeRawBurstParameter(string parameter)
        {
            parameter = parameter.Trim();
            if (parameter.EndsWith("&", StringComparison.Ordinal))
                parameter = parameter[..^1].TrimEnd();

            if (parameter.Length == 0)
                return false;

            return char.IsUpper(parameter[0])
                   || parameter.IndexOf('.') >= 0
                   || parameter.IndexOf('`') >= 0
                   || parameter.IndexOf('<') >= 0
                   || parameter.IndexOf('*') >= 0;
        }

        static void AppendRawBurstParameters(StringBuilder builder, string parameters)
        {
            var start = 0;
            var depth = 0;
            var appendedAny = false;
            for (var index = 0; index <= parameters.Length; index++)
            {
                if (index < parameters.Length)
                {
                    if (parameters[index] is '<' or '[')
                    {
                        depth++;
                        continue;
                    }

                    if (parameters[index] is '>' or ']')
                    {
                        if (depth > 0)
                            depth--;

                        continue;
                    }

                    if (parameters[index] != '|' || depth != 0)
                        continue;
                }

                AppendRawBurstParameter(builder, parameters[start..index], ref appendedAny);
                start = index + 1;
            }
        }

        static void AppendRawBurstParameter(StringBuilder builder, string parameter, ref bool appendedAny)
        {
            parameter = parameter.Trim();
            if (parameter.Length == 0)
                return;

            if (appendedAny)
                builder.Append(", ");

            appendedAny = true;
            if (parameter.EndsWith("&", StringComparison.Ordinal))
            {
                builder.Append("ref ");
                builder.Append(parameter, 0, parameter.Length - 1);
                return;
            }

            builder.Append(parameter);
        }
    }
}
