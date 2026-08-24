#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class BurstInstructionParser
    {
        static readonly string[] wasmVectorPrefixes = { "i8x16.", "i16x8.", "i32x4.", "i64x2.", "f32x4.", "f64x2." };

        internal static void SplitOperands(string operands, List<string> result)
        {
            result.Clear();
            var start = 0;
            var depth = 0;
            var quoted = false;
            // quoted symbols and generic signatures can contain commas; only top-level commas separate operands.
            for (int index = 0, n = operands.Length; index <= n; ++index)
            {
                if (index < n)
                {
                    var character = operands[index];
                    if (character == '"')
                    {
                        quoted = !quoted;
                        continue;
                    }

                    if (quoted)
                        continue;
                    if (character is '[' or '(' or '{' or '<')
                    {
                        depth++;
                        continue;
                    }

                    if (character is ']' or ')' or '}' or '>')
                    {
                        if (depth > 0)
                            depth--;
                        continue;
                    }

                    if (character != ',' || depth != 0)
                        continue;
                }

                var operand = operands.AsSpan(start, index - start).Trim();
                if (operand.Length > 0)
                    result.Add(operand.ToString());
                start = index + 1;
            }
        }

        internal static bool TryParseCodeLabel(string line, out string label)
        {
            label = string.Empty;
            var text = line.AsSpan().Trim();
            if (text.Length == 0 || text[^1] != ':')
                return false;

            var labelSpan = text[..^1].Trim().Trim('"');
            if (labelSpan.Length == 0)
                return false;

            label = labelSpan.ToString();
            return true;
        }

        internal static bool TryGetDirectBranchTarget(IReadOnlyList<string> operands, out string target)
        {
            target = string.Empty;
            if (operands.Count == 0)
                return false;

            var value = operands[^1].Trim();
            if (value.StartsWith("short ", StringComparison.OrdinalIgnoreCase))
                value = value[6..].TrimStart();
            if (value.StartsWith("near ", StringComparison.OrdinalIgnoreCase))
                value = value[5..].TrimStart();
            if (HasMemorySyntax(value) || IsRegisterOperand(value))
                return false;

            target = value.Trim('"');
            return target.Length > 0;
        }

        internal static bool TryParseInstruction(string line, out string mnemonic, out string operands)
        {
            mnemonic = string.Empty;
            operands = string.Empty;
            var text = line.AsSpan().Trim();
            if (text.Length == 0
                || text[0] is '#' or ';'
                || text.StartsWith("//", StringComparison.Ordinal)
                || text[0] == '.'
                || IsFunctionLabel(text, out _))
                return false;

            var firstEnd = ReadTokenEnd(text, 0);
            if (firstEnd == 0)
                return false;

            var first = text[..firstEnd].ToString().ToLowerInvariant();
            var operandStart = firstEnd;
            while (operandStart < text.Length && char.IsWhiteSpace(text[operandStart]))
                ++operandStart;
            if (first is "lock" or "rep" or "repe" or "repne")
            {
                var secondEnd = ReadTokenEnd(text, operandStart);
                if (secondEnd <= operandStart)
                    return false;

                mnemonic = $"{first} {text[operandStart..secondEnd].ToString().ToLowerInvariant()}";
                operands = secondEnd < text.Length ? text[secondEnd..].Trim().ToString() : string.Empty;
                return true;
            }

            mnemonic = first;
            operands = operandStart < text.Length ? text[operandStart..].Trim().ToString() : string.Empty;
            return true;
        }

        internal static int ReadTokenEnd(ReadOnlySpan<char> text, int start)
        {
            var index = start;
            while (index < text.Length)
            {
                var character = text[index];
                if (!char.IsLetterOrDigit(character) && character is not '_' and not '.')
                    break;

                ++index;
            }

            return index;
        }

        internal static bool IsScalarSimdMnemonic(string mnemonic) =>
            mnemonic.EndsWith("ss", StringComparison.Ordinal)
            || mnemonic.EndsWith("sd", StringComparison.Ordinal);

        internal static bool IsPackedVectorMnemonic(string mnemonic) =>
            mnemonic.StartsWith("v", StringComparison.Ordinal) && !IsScalarSimdMnemonic(mnemonic)
            || mnemonic.StartsWith("v128.", StringComparison.Ordinal)
            || StartsWithAny(mnemonic, wasmVectorPrefixes)
            || mnemonic.StartsWith("padd", StringComparison.Ordinal)
            || mnemonic.StartsWith("psub", StringComparison.Ordinal)
            || mnemonic.StartsWith("pmul", StringComparison.Ordinal)
            || mnemonic.StartsWith("pand", StringComparison.Ordinal)
            || mnemonic.StartsWith("por", StringComparison.Ordinal)
            || mnemonic.StartsWith("pxor", StringComparison.Ordinal)
            || mnemonic.EndsWith("ps", StringComparison.Ordinal)
            || mnemonic.EndsWith("pd", StringComparison.Ordinal);

        internal static bool StartsWithAny(string value, string[] prefixes)
        {
            foreach (var prefix in prefixes)
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                    return true;

            return false;
        }

        internal static bool IsConditionalBranch(string mnemonic, IReadOnlyList<string> operands)
        {
            if (mnemonic is "cbz" or "cbnz" or "tbz" or "tbnz" or "br_if")
                return true;

            if (operands.Count > 0 && mnemonic.StartsWith("loop", StringComparison.Ordinal))
                return true;

            if (mnemonic.StartsWith("b.", StringComparison.Ordinal))
                return true;

            return mnemonic.StartsWith("j", StringComparison.Ordinal) && mnemonic != "jmp";
        }

        internal static bool IsUnconditionalBranch(string mnemonic) =>
            mnemonic is "jmp" or "b" or "br";

        internal static bool IsCall(string mnemonic) =>
            mnemonic is "call" or "call_indirect" or "bl" or "blr";

        internal static bool IsReturn(string mnemonic) =>
            mnemonic.StartsWith("ret", StringComparison.Ordinal)
            || mnemonic == "end_function";

        internal static bool HasStackOrFrameOperand(string operands)
        {
            var text = operands.AsSpan();
            for (var start = 0; start < text.Length;)
            {
                while (start < text.Length && !char.IsLetterOrDigit(text[start]))
                    start++;
                var end = start;
                while (end < text.Length && char.IsLetterOrDigit(text[end]))
                    end++;

                var token = text[start..end];
                if (token.Equals("rsp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("rbp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("esp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("ebp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("sp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("fp", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("x29", StringComparison.OrdinalIgnoreCase))
                    return true;

                start = end;
            }

            return false;
        }

        internal static BurstRegisterKinds ClassifyRegisters(string operands)
        {
            var result = BurstRegisterKinds.None;
            var text = operands.AsSpan();
            for (var start = 0; start < text.Length;)
            {
                while (start < text.Length && !char.IsLetterOrDigit(text[start]))
                    start++;
                var end = start;
                while (end < text.Length && char.IsLetterOrDigit(text[end]))
                    end++;

                var token = text[start..end];
                if (token.Length > 3 && char.IsDigit(token[3]))
                {
                    if (token[..3].Equals("xmm", StringComparison.OrdinalIgnoreCase))
                        result |= BurstRegisterKinds.Xmm;
                    else if (token[..3].Equals("ymm", StringComparison.OrdinalIgnoreCase))
                        result |= BurstRegisterKinds.Ymm;
                    else if (token[..3].Equals("zmm", StringComparison.OrdinalIgnoreCase))
                        result |= BurstRegisterKinds.Zmm;
                }

                if (token.Length > 1 && char.IsDigit(token[1]))
                    switch (char.ToLowerInvariant(token[0]))
                    {
                        case 'v':
                        case 'q':
                            result |= BurstRegisterKinds.ArmVector;
                            break;
                        case 'z':
                            result |= BurstRegisterKinds.ArmVector
                                      | BurstRegisterKinds.ScalableArmVector;
                            break;
                        case 's':
                        case 'd':
                            result |= BurstRegisterKinds.ArmScalar;
                            break;
                    }

                start = end;
            }

            return result;
        }

        internal static bool IsFunctionLabel(ReadOnlySpan<char> line, out string label)
        {
            label = string.Empty;
            if (line.Length == 0 || line[^1] != ':')
                return false;

            var labelSpan = line[..^1].Trim();
            if (labelSpan.Length == 0 || labelSpan.StartsWith(".L", StringComparison.Ordinal))
                return false;

            if (labelSpan.StartsWith(".seh", StringComparison.Ordinal)
                || labelSpan.StartsWith(".cv", StringComparison.Ordinal))
                return false;

            label = labelSpan.ToString();
            return true;
        }

        internal static bool IsDirectCall(string mnemonic, IReadOnlyList<string> operands)
        {
            if (BaseMnemonic(mnemonic) is "blr" or "call_indirect")
                return false;
            if (operands.Count == 0 || HasMemorySyntax(operands[0]))
                return false;

            return !IsRegisterOperand(operands[0]);
        }

        internal static string CleanTransferTarget(string value)
        {
            var target = value.Trim();
            if (target.EndsWith("@PLT", StringComparison.OrdinalIgnoreCase))
                target = target[..^4];

            return target.Trim().Trim('"');
        }

        internal static bool IsNumericImmediate(string operand)
        {
            var value = operand.Trim();
            if (value.StartsWith("-", StringComparison.Ordinal) || value.StartsWith("+", StringComparison.Ordinal))
                value = value[1..];
            var hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (hexadecimal)
                value = value[2..];
            if (value.Length == 0)
                return false;

            foreach (var character in value)
                if (!char.IsDigit(character)
                    && (!hexadecimal || character is not (>= 'a' and <= 'f') and not (>= 'A' and <= 'F')))
                    return false;

            return true;
        }

        internal static string BaseMnemonic(string mnemonic)
        {
            var space = mnemonic.LastIndexOf(' ');
            return space < 0 ? mnemonic : mnemonic[(space + 1)..];
        }

        internal static bool HasMemorySyntax(string operand) =>
            operand.IndexOf('[', StringComparison.Ordinal) >= 0
            && operand.IndexOf(']', StringComparison.Ordinal) >= 0;

        internal static bool IsRegisterOperand(string operand)
        {
            var value = operand.Trim().TrimStart('%');
            var dot = value.IndexOf('.');
            if (dot >= 0)
                value = value[..dot];
            var bracket = value.IndexOf('[');
            if (bracket >= 0)
                value = value[..bracket];
            if (value.Length == 0
                || value.IndexOf(' ') >= 0
                || value.IndexOf('+') >= 0
                || value.IndexOf('-') >= 0
                || value.IndexOf('*') >= 0)
                return false;

            if (value is "rax" or "rbx" or "rcx" or "rdx" or "rsi" or "rdi" or "rbp" or "rsp"
                or "eax" or "ebx" or "ecx" or "edx" or "esi" or "edi" or "ebp" or "esp"
                or "ax" or "bx" or "cx" or "dx" or "si" or "di" or "bp" or "sp"
                or "al" or "bl" or "cl" or "dl" or "sil" or "dil" or "bpl" or "spl"
                or "lr" or "fp" or "xzr" or "wzr")
                return true;

            return RegisterHasNumericSuffix(value, "r")
                   || RegisterHasNumericSuffix(value, "x")
                   || RegisterHasNumericSuffix(value, "w")
                   || RegisterHasNumericSuffix(value, "v")
                   || RegisterHasNumericSuffix(value, "q")
                   || RegisterHasNumericSuffix(value, "s")
                   || RegisterHasNumericSuffix(value, "d")
                   || RegisterHasNumericSuffix(value, "z")
                   || RegisterHasNumericSuffix(value, "p")
                   || RegisterHasNumericSuffix(value, "xmm")
                   || RegisterHasNumericSuffix(value, "ymm")
                   || RegisterHasNumericSuffix(value, "zmm")
                   || RegisterHasNumericSuffix(value, "k");
        }

        static bool RegisterHasNumericSuffix(string value, string prefix)
        {
            if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || value.Length == prefix.Length)
                return false;

            for (int index = prefix.Length, n = value.Length; index < n; ++index)
                if (!char.IsDigit(value[index]))
                    return false;

            return true;
        }
    }
}
