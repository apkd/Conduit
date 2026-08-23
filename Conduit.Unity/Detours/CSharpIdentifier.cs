#nullable enable

using System.Globalization;
using System.Text;

namespace Conduit
{
    /// <summary>Validates and escapes metadata identifiers for generated C# declarations.</summary>
    static class CSharpIdentifier
    {
        public static string Escape(string value)
            => IsKeyword(value) ? "@" + value : value;

        public static string EscapeQualified(string value)
        {
            if (value.IndexOf('.') < 0)
                return Escape(value);

            var builder = new StringBuilder(value.Length);
            var start = 0;
            for (var index = 0; index <= value.Length; index++)
            {
                if (index < value.Length && value[index] != '.')
                    continue;

                if (builder.Length > 0)
                    builder.Append('.');
                builder.Append(Escape(value.Substring(start, index - start)));
                start = index + 1;
            }

            return builder.ToString();
        }

        public static bool IsValid(string value)
        {
            if (value.Length == 0 || !IsStart(value[0]))
                return false;
            for (var index = 1; index < value.Length; index++)
                if (!IsPart(value[index]))
                    return false;
            return true;
        }

        static bool IsStart(char value) => value == '_' || char.IsLetter(value);

        static bool IsPart(char value)
            => value == '_'
               || char.IsLetterOrDigit(value)
               || char.GetUnicodeCategory(value) is UnicodeCategory.ConnectorPunctuation
                   or UnicodeCategory.NonSpacingMark
                   or UnicodeCategory.SpacingCombiningMark
                   or UnicodeCategory.Format;

        static bool IsKeyword(string value)
            => value is "abstract" or "as" or "base" or "bool" or "break" or "byte"
                or "case" or "catch" or "char" or "checked" or "class" or "const" or "continue"
                or "decimal" or "default" or "delegate" or "do" or "double" or "else" or "enum"
                or "event" or "explicit" or "extern" or "false" or "finally" or "fixed" or "float"
                or "for" or "foreach" or "goto" or "if" or "implicit" or "in" or "int" or "interface"
                or "internal" or "is" or "lock" or "long" or "namespace" or "new" or "null" or "object"
                or "operator" or "out" or "override" or "params" or "private" or "protected" or "public"
                or "readonly" or "ref" or "return" or "sbyte" or "sealed" or "short" or "sizeof"
                or "stackalloc" or "static" or "string" or "struct" or "switch" or "this" or "throw"
                or "true" or "try" or "typeof" or "uint" or "ulong" or "unchecked" or "unsafe"
                or "ushort" or "using" or "virtual" or "void" or "volatile" or "while";
    }
}
