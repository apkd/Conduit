using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

static class MethodSelectorSignatureComparer
{
    internal static bool Equals(ReadOnlySpan<char> selector, ReadOnlySpan<char> canonical)
    {
        var selectorTokens = SyntaxFactory.ParseTokens(selector.ToString()).ToArray();
        var canonicalTokens = SyntaxFactory.ParseTokens(canonical.ToString()).ToArray();
        var selectorIndex = 0;
        var canonicalIndex = 0;

        while (selectorIndex < selectorTokens.Length && canonicalIndex < canonicalTokens.Length)
        {
            SkipGlobalQualifier(selectorTokens, ref selectorIndex);
            SkipGlobalQualifier(canonicalTokens, ref canonicalIndex);

            var nextSelectorIndex = selectorIndex;
            var nextCanonicalIndex = canonicalIndex;
            if (TryReadSpecialType(selectorTokens, ref nextSelectorIndex, out var selectorType))
            {
                if (!TryReadSpecialType(canonicalTokens, ref nextCanonicalIndex, out var canonicalType)
                    || selectorType != canonicalType)
                    return false;

                selectorIndex = nextSelectorIndex;
                canonicalIndex = nextCanonicalIndex;
                continue;
            }

            if (selectorTokens[selectorIndex].RawKind != canonicalTokens[canonicalIndex].RawKind
                || selectorTokens[selectorIndex].ValueText != canonicalTokens[canonicalIndex].ValueText)
                return false;

            selectorIndex++;
            canonicalIndex++;
        }

        SkipGlobalQualifier(selectorTokens, ref selectorIndex);
        SkipGlobalQualifier(canonicalTokens, ref canonicalIndex);
        return selectorIndex == selectorTokens.Length && canonicalIndex == canonicalTokens.Length;
    }

    static void SkipGlobalQualifier(SyntaxToken[] tokens, ref int index)
    {
        if (index + 1 < tokens.Length
            && tokens[index].Text == "global"
            && tokens[index + 1].IsKind(SyntaxKind.ColonColonToken))
            index += 2;
    }

    static bool TryReadSpecialType(
        SyntaxToken[] tokens,
        ref int index,
        out SpecialType type)
    {
        if (index >= tokens.Length)
        {
            type = SpecialType.None;
            return false;
        }

        if (TryGetKeywordType(tokens[index].Text, out type))
        {
            index++;
            return true;
        }

        // canonical selectors use C# keywords, while reflection commonly exposes CLR type names.
        if (index + 2 < tokens.Length
            && tokens[index].ValueText == "System"
            && tokens[index + 1].IsKind(SyntaxKind.DotToken)
            && TryGetClrType(tokens[index + 2].ValueText, out type))
        {
            index += 3;
            return true;
        }

        type = SpecialType.None;
        return false;
    }

    static bool TryGetKeywordType(string name, out SpecialType type)
    {
        type = name switch
        {
            "bool" => SpecialType.System_Boolean,
            "byte" => SpecialType.System_Byte,
            "sbyte" => SpecialType.System_SByte,
            "char" => SpecialType.System_Char,
            "decimal" => SpecialType.System_Decimal,
            "double" => SpecialType.System_Double,
            "float" => SpecialType.System_Single,
            "int" => SpecialType.System_Int32,
            "uint" => SpecialType.System_UInt32,
            "long" => SpecialType.System_Int64,
            "ulong" => SpecialType.System_UInt64,
            "short" => SpecialType.System_Int16,
            "ushort" => SpecialType.System_UInt16,
            "object" => SpecialType.System_Object,
            "string" => SpecialType.System_String,
            "void" => SpecialType.System_Void,
            "nint" => SpecialType.System_IntPtr,
            "nuint" => SpecialType.System_UIntPtr,
            _ => SpecialType.None,
        };
        return type != SpecialType.None;
    }

    static bool TryGetClrType(string name, out SpecialType type)
    {
        type = name switch
        {
            "Boolean" => SpecialType.System_Boolean,
            "Byte" => SpecialType.System_Byte,
            "SByte" => SpecialType.System_SByte,
            "Char" => SpecialType.System_Char,
            "Decimal" => SpecialType.System_Decimal,
            "Double" => SpecialType.System_Double,
            "Single" => SpecialType.System_Single,
            "Int32" => SpecialType.System_Int32,
            "UInt32" => SpecialType.System_UInt32,
            "Int64" => SpecialType.System_Int64,
            "UInt64" => SpecialType.System_UInt64,
            "Int16" => SpecialType.System_Int16,
            "UInt16" => SpecialType.System_UInt16,
            "Object" => SpecialType.System_Object,
            "String" => SpecialType.System_String,
            "Void" => SpecialType.System_Void,
            "IntPtr" => SpecialType.System_IntPtr,
            "UIntPtr" => SpecialType.System_UIntPtr,
            _ => SpecialType.None,
        };
        return type != SpecialType.None;
    }
}
