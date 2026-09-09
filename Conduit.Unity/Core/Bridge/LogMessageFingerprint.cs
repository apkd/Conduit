#nullable enable

using System;

namespace Conduit
{
    // lossy lexical grouping avoids allocating normalized strings or comparing pairs of messages
    static class LogMessageFingerprint
    {
        internal static ulong Compute(ReadOnlySpan<char> message)
        {
            var hash = 14695981039346656037UL;
            var whitespace = false;
            for (var index = 0; index < message.Length;)
            {
                var value = message[index];
                if (char.IsWhiteSpace(value))
                {
                    if (!whitespace)
                        Append(' ');
                    whitespace = true;
                    index++;
                    continue;
                }

                whitespace = false;
                if (value is '\'' or '"')
                {
                    var quote = value;
                    index++;
                    while (index < message.Length)
                    {
                        var character = message[index++];
                        if (character == '\\' && index < message.Length)
                            index++;
                        else if (character == quote)
                            break;
                    }
                    Append('\0');
                    continue;
                }

                if (index == 0 || !IsIdentifier(message[index - 1]))
                {
                    var remaining = message.Slice(index);
                    var guidLength = GuidLength(remaining);
                    if (guidLength > 0)
                    {
                        index += guidLength;
                        Append('\0');
                        continue;
                    }

                    var length = NumberLength(remaining);
                    if (length > 0)
                    {
                        index += length;
                        Append('\0');
                        continue;
                    }
                }

                Append(value);
                index++;
            }
            return hash;

            void Append(char value) => hash = unchecked((hash ^ value) * 1099511628211UL);
        }

        static int NumberLength(ReadOnlySpan<char> value)
        {
            var index = value[0] is '+' or '-' ? 1 : 0;
            if (index == value.Length || !IsDigit(value[index]))
                return 0;

            if (value.Length - index > 2 && value[index] == '0' && value[index + 1] is 'x' or 'X')
            {
                index += 2;
                var start = index;
                while (index < value.Length && IsHex(value[index]))
                    index++;
                if (index == start)
                    return 0;
            }
            else
            {
                while (index < value.Length && IsDigit(value[index]))
                    index++;
                if (index + 1 < value.Length && value[index] == '.' && IsDigit(value[index + 1]))
                {
                    index++;
                    while (index < value.Length && IsDigit(value[index]))
                        index++;
                }
                if (index < value.Length && value[index] is 'e' or 'E')
                {
                    var exponent = index++;
                    if (index < value.Length && value[index] is '+' or '-')
                        index++;
                    var start = index;
                    while (index < value.Length && IsDigit(value[index]))
                        index++;
                    if (index == start)
                        index = exponent;
                }
            }

            return index < value.Length && IsIdentifier(value[index]) ? 0 : index;
        }

        static int GuidLength(ReadOnlySpan<char> value)
        {
            if (value.Length < 32)
                return 0;

            var length = value[8] == '-' ? 36 : 32;
            if (value.Length < length || value.Length > length && IsIdentifier(value[length]))
                return 0;

            for (var index = 0; index < length; index++)
                if (length == 36 && index is 8 or 13 or 18 or 23 ? value[index] != '-' : !IsHex(value[index]))
                    return 0;
            return length;
        }

        static bool IsIdentifier(char value) => char.IsLetterOrDigit(value) || value == '_';
        static bool IsDigit(char value) => value is >= '0' and <= '9';
        static bool IsHex(char value) => IsDigit(value) || value is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }
}
