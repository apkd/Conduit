#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Conduit
{

    static class ProjectSettingKey
    {
        static readonly char[] tokenSeparators = { '.', '_' };

        internal static string Canonicalize(string value)
        {
            if (IsCanonical(value.AsSpan()))
                return value;

            return Canonicalize(value.AsSpan());
        }

        internal static string Canonicalize(ReadOnlySpan<char> value)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.EnsureCapacity(value.Length + 8);
            var previous = CharacterKind.Separator;
            var trimmed = value.Trim();

            for (int index = 0, count = trimmed.Length; index < count; ++index)
            {
                char character = trimmed[index];
                if (character is '.' or '/')
                {
                    AppendHierarchySeparator(builder);
                    previous = CharacterKind.Separator;
                    continue;
                }
                if (character == '_' || character == '-' || char.IsWhiteSpace(character))
                {
                    AppendWordSeparator(builder);
                    previous = CharacterKind.Separator;
                    continue;
                }

                var kind = GetKind(character);
                var next = index + 1 < count
                    ? GetKind(trimmed[index + 1])
                    : CharacterKind.Separator;
                if (kind == CharacterKind.Separator)
                {
                    AppendWordSeparator(builder);
                    previous = kind;
                    continue;
                }

                if (kind == CharacterKind.Upper
                    && (previous is CharacterKind.Lower or CharacterKind.Digit
                        || previous == CharacterKind.Upper && next == CharacterKind.Lower))
                    AppendWordSeparator(builder);

                builder.Append(char.ToLowerInvariant(character));
                previous = kind;
            }

            while (builder.Length > 0 && builder[^1] is '.' or '_')
                builder.Length--;

            return builder.ToString();

            static CharacterKind GetKind(char value)
                => char.IsUpper(value)
                    ? CharacterKind.Upper
                    : char.IsLower(value)
                        ? CharacterKind.Lower
                        : char.IsDigit(value)
                            ? CharacterKind.Digit
                            : CharacterKind.Separator;

            static void AppendHierarchySeparator(StringBuilder builder)
            {
                while (builder.Length > 0 && builder[^1] == '_')
                    builder.Length--;
                if (builder.Length > 0 && builder[^1] != '.')
                    builder.Append('.');
            }

            static void AppendWordSeparator(StringBuilder builder)
            {
                if (builder.Length > 0 && builder[^1] is not ('.' or '_'))
                    builder.Append('_');
            }

        }

        static bool IsCanonical(ReadOnlySpan<char> candidate)
        {
            if (candidate.Length == 0
                || candidate[0] is '.' or '_'
                || candidate[^1] is '.' or '_')
                return false;

            bool previousWasSeparator = false;
            foreach (var character in candidate)
            {
                if (character is '.' or '_')
                {
                    if (previousWasSeparator)
                        return false;

                    previousWasSeparator = true;
                    continue;
                }

                if (!char.IsLower(character) && !char.IsDigit(character))
                    return false;
                previousWasSeparator = false;
            }

            return true;
        }

        internal static string Compact(string key)
        {
            int separatorCount = 0;
            foreach (var character in key)
                if (character is '.' or '_')
                    separatorCount++;

            if (separatorCount == 0)
                return key;

            return string.Create(
                key.Length - separatorCount,
                key,
                static (result, source) =>
                {
                    int index = 0;
                    foreach (var character in source)
                        if (character is not ('.' or '_'))
                            result[index++] = character;
                }
            );
        }

        internal static string[] Tokens(string key)
            => key.Split(tokenSeparators, StringSplitOptions.RemoveEmptyEntries);

        enum CharacterKind
        {
            Separator,
            Lower,
            Upper,
            Digit,
        }
    }
}
