#nullable enable

namespace Conduit
{
    static class TestNameFilter
    {
        internal static string? Normalize(string? rawFilter)
        {
            if (rawFilter == null)
                return null;

            var trimmed = rawFilter.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        internal static string ToRegexPattern(string filter)
        {
            var effectivePattern = filter.IndexOf('*') >= 0 || filter.IndexOf('?') >= 0
                ? filter
                : $"*{filter}*";
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append('^');
            foreach (var character in effectivePattern)
            {
                switch (character)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '?':
                        builder.Append('.');
                        break;
                    default:
                        AppendEscapedRegexCharacter(builder, character);
                        break;
                }
            }

            builder.Append('$');
            return builder.ToString();
        }

        static void AppendEscapedRegexCharacter(System.Text.StringBuilder builder, char character)
        {
            switch (character)
            {
                case '\\':
                case '.':
                case '$':
                case '^':
                case '{':
                case '[':
                case '(':
                case '|':
                case ')':
                case '+':
                case ']':
                case '}':
                    builder.Append('\\');
                    break;
            }

            builder.Append(character);
        }
    }
}
