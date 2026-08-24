#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class BurstFunctionSelector
    {
        const int MaxForwarderInstructions = 8;

        internal static bool TryGetForwardedBlock(
            string[] lines,
            BurstAsmFunctionBlock block,
            IReadOnlyList<BurstAsmFunctionBlock> blocks,
            out BurstAsmFunctionBlock forwarded)
        {
            forwarded = default;
            if (block.InstructionCount > MaxForwarderInstructions)
                return false;

            var target = string.Empty;
            using var pooledOperands = ConduitPool.GetPooledList<string>(out var parsedOperands);
            for (int index = block.Start; index < block.End; ++index)
            {
                if (!BurstInstructionParser.TryParseInstruction(lines[index], out var mnemonic, out var operands))
                    continue;

                BurstInstructionParser.SplitOperands(operands, parsedOperands);
                if (BurstInstructionParser.IsCall(mnemonic) && BurstInstructionParser.IsDirectCall(mnemonic, parsedOperands))
                {
                    if (!SetTarget(parsedOperands[0]))
                        return false;
                    continue;
                }

                if (BurstInstructionParser.IsUnconditionalBranch(mnemonic)
                    && BurstInstructionParser.TryGetDirectBranchTarget(parsedOperands, out var branchTarget))
                {
                    if (!SetTarget(branchTarget))
                        return false;
                    continue;
                }

                if (!IsScaffolding(BurstInstructionParser.BaseMnemonic(mnemonic), parsedOperands))
                    return false;
            }

            if (target.Length == 0)
                return false;

            foreach (var candidate in blocks)
            {
                if (!string.Equals(BurstInstructionParser.CleanTransferTarget(candidate.Label), target, StringComparison.Ordinal))
                    continue;

                forwarded = candidate;
                return true;
            }

            return false;

            bool SetTarget(string value)
            {
                value = BurstInstructionParser.CleanTransferTarget(value);
                if (value.Length == 0 || target.Length > 0 && target != value)
                    return false;

                target = value;
                return true;
            }

            static bool IsScaffolding(string mnemonic, IReadOnlyList<string> operands)
            {
                if (mnemonic is "ret" or "push" or "pop" or "nop" or "endbr64" or "vzeroupper")
                    return true;
                if (mnemonic == "mov" && operands.Count == 2)
                    return BurstInstructionParser.IsRegisterOperand(operands[0])
                           && BurstInstructionParser.IsRegisterOperand(operands[1]);
                if ((mnemonic is "add" or "sub") && operands.Count == 2)
                    return operands[0].Trim().TrimStart('%') is "rsp" or "esp" or "sp"
                           && BurstInstructionParser.IsNumericImmediate(operands[1]);

                return false;
            }
        }

        internal static void GetFunctionBlocks(
            string[] lines,
            List<BurstAsmFunctionBlock> blocks)
        {
            var currentLabel = string.Empty;
            var start = -1;
            for (int index = 0, n = lines.Length; index < n; ++index)
            {
                var line = lines[index].AsSpan().Trim();
                if (line.StartsWith(".section", StringComparison.Ordinal)
                    || line.StartsWith(".text", StringComparison.Ordinal))
                {
                    Flush(index);
                    continue;
                }

                if (!BurstInstructionParser.IsFunctionLabel(line, out var label))
                    continue;

                Flush(index);
                currentLabel = label;
                start = index + 1;
            }

            Flush(lines.Length);

            void Flush(int end)
            {
                if (start < 0)
                    return;

                int instructionCount = 0;
                for (int index = start; index < end; ++index)
                    if (IsInstructionLine(lines[index]))
                        instructionCount++;

                if (instructionCount > 0)
                    blocks.Add(new(currentLabel, start, end, instructionCount));

                currentLabel = string.Empty;
                start = -1;
            }
        }

        static bool IsInstructionLine(string line)
        {
            var text = line.AsSpan().Trim();
            return text.Length > 0
                   && text[0] is not ('#' or ';' or '.')
                   && !text.StartsWith("//", StringComparison.Ordinal)
                   && text[^1] != ':';
        }

        internal static BurstAsmFunctionBlock SelectMainBlock(BurstTarget target, IReadOnlyList<BurstAsmFunctionBlock> blocks)
        {
            var displayName = NormalizeAsmText(BurstSymbolFormatter.CleanDisplayName(target.DisplayName));
            var jobType = target.JobTypeName.Length == 0
                ? string.Empty
                : NormalizeAsmText(BurstTypeNameFormatter.ShortTypeName(target.JobTypeName));
            var declaringType = target.DeclaringTypeName.Length == 0
                ? string.Empty
                : NormalizeAsmText(BurstTypeNameFormatter.ShortTypeName(target.DeclaringTypeName));
            var method = NormalizeAsmText(target.MethodName);
            var best = blocks[0];
            var bestScore = int.MinValue;
            foreach (var block in blocks)
            {
                var score = Score(block);
                if (score <= bestScore)
                    continue;

                best = block;
                bestScore = score;
            }

            return best;

            int Score(BurstAsmFunctionBlock block)
            {
                var label = NormalizeAsmText(block.Label);
                if (label.Length == 0)
                    return int.MinValue;

                // semantic matches dominate; body size only breaks ties between similarly named blocks.
                var score = Math.Min(block.InstructionCount, 1000);
                if (label.StartsWith("burstinitialize", StringComparison.Ordinal) || label == "feat00")
                    score -= 100000;
                if (IsHexLabel(label))
                    score -= 10000;
                if (displayName.Length > 0 && (label.Contains(displayName) || displayName.Contains(label)))
                    score += 20000;
                if (jobType.Length > 0 && label.Contains(jobType))
                    score += 8000;
                if (declaringType.Length > 0 && label.Contains(declaringType))
                    score += 4000;
                if (method.Length > 0)
                {
                    if (HasExactMethodName(block.Label))
                        score += 12000;
                    else if (label.Contains(method))
                        score += method == "execute" ? 500 : 2000;
                }
                if (label.Contains("jobstruct"))
                    score += 500;

                return score;
            }

            bool HasExactMethodName(string label)
            {
                var signatureStart = label.IndexOf('(');
                var name = (signatureStart < 0 ? label : label[..signatureStart]).Trim().Trim('"');
                var separator = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
                if (separator >= 0)
                    name = name[(separator + 1)..];

                return NormalizeAsmText(name) == method;
            }

            static bool IsHexLabel(string label)
            {
                if (label.Length is < 8 or > 32)
                    return false;

                foreach (var character in label)
                    if (!BurstSymbolFormatter.IsHex(character))
                        return false;

                return true;
            }
        }

        internal static string NormalizeAsmText(string value)
        {
            var length = 0;
            var unchanged = true;
            foreach (var character in value)
                if (char.IsLetterOrDigit(character))
                {
                    length++;
                    unchanged &= char.ToLowerInvariant(character) == character;
                }
                else
                    unchanged = false;

            if (unchanged)
                return value;

            return string.Create(length, value, static (destination, source) =>
            {
                var index = 0;
                foreach (var character in source)
                    if (char.IsLetterOrDigit(character))
                        destination[index++] = char.ToLowerInvariant(character);
            });
        }
    }
}
