#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class BurstInstructionAnalyzer
    {
        static readonly string[] fusedMultiplyAddPrefixes =
        {
            "vfmadd", "vfmsub", "vfnmadd", "vfnmsub",
            "fmadd", "fmsub", "fnmadd", "fnmsub",
            "fmla", "fmls", "fmad", "fmsb", "fnmad", "fnmsb",
        };
        internal static BurstInstructionFacts AnalyzeInstruction(
            BurstAsmStats stats,
            string mnemonic,
            string operands,
            IReadOnlyList<string> parsedOperands)
        {
            var baseMnemonic = BurstInstructionParser.BaseMnemonic(mnemonic);
            var isXor = IsXorMnemonic(baseMnemonic);
            var isZeroingXor = isXor && IsZeroingIdiom(parsedOperands);
            AnalyzeNotableOpcode(stats, mnemonic, baseMnemonic, parsedOperands);

            if (isXor)
            {
                stats.XorInstructionCount++;
                if (isZeroingXor)
                    stats.ZeroingXorInstructionCount++;
            }

            if (baseMnemonic == "movabs")
            {
                var source = parsedOperands.Count > 1 ? parsedOperands[1] : string.Empty;
                if (BurstInstructionParser.IsNumericImmediate(source))
                    stats.NumericMovabsCount++;
                else
                    stats.SymbolMovabsCount++;
            }

            if (baseMnemonic is "push" or "vpush")
                stats.PushInstructionCount++;
            else if (baseMnemonic is "pop" or "vpop")
                stats.PopInstructionCount++;

            if (BurstInstructionParser.IsCall(mnemonic))
                AnalyzeCall(stats, mnemonic, parsedOperands);
            else if (BurstInstructionParser.IsReturn(mnemonic))
                stats.ReturnCount++;

            var memoryAccess = BurstMemoryAnalyzer.ClassifyMemoryAccess(baseMnemonic, parsedOperands);
            if (memoryAccess != BurstMemoryAccessKind.None)
            {
                switch (memoryAccess)
                {
                    case BurstMemoryAccessKind.Load:
                        stats.LoadInstructionCount++;
                        break;
                    case BurstMemoryAccessKind.Store:
                        stats.StoreInstructionCount++;
                        break;
                    case BurstMemoryAccessKind.ReadModifyWrite:
                        stats.ReadModifyWriteInstructionCount++;
                        break;
                    default:
                        stats.OtherMemoryInstructionCount++;
                        break;
                }

                if (BurstInstructionParser.HasStackOrFrameOperand(operands))
                    stats.StackFrameMemoryInstructionCount++;
                if (operands.IndexOf(".lcpi", StringComparison.OrdinalIgnoreCase) >= 0
                    || operands.IndexOf(".lconst", StringComparison.OrdinalIgnoreCase) >= 0)
                    stats.ConstantPoolMemoryInstructionCount++;
            }

            if (BurstMemoryAnalyzer.IsAddressGeneration(baseMnemonic))
                stats.AddressGenerationInstructionCount++;

            var simdRole = BurstSimdAnalyzer.ClassifySimd(baseMnemonic, operands, isZeroingXor);
            switch (simdRole)
            {
                case BurstSimdRole.Transfer:
                    stats.SimdTransferInstructionCount++;
                    break;
                case BurstSimdRole.Lane:
                    stats.SimdLaneInstructionCount++;
                    break;
                case BurstSimdRole.ScalarCompute:
                    stats.SimdScalarComputeInstructionCount++;
                    break;
                case BurstSimdRole.PackedCompute:
                    stats.SimdPackedComputeInstructionCount++;
                    BurstSimdAnalyzer.RecordPackedComputeWidth(stats, operands);
                    break;
                case BurstSimdRole.Setup:
                    stats.SimdSetupInstructionCount++;
                    break;
                case BurstSimdRole.Other:
                    stats.SimdOtherInstructionCount++;
                    break;
            }

            var callKind = BurstInstructionParser.IsCall(mnemonic)
                ? BurstInstructionParser.IsDirectCall(mnemonic, parsedOperands) ? BurstCallKind.Direct : BurstCallKind.Indirect
                : BurstCallKind.None;
            return new(memoryAccess, simdRole, callKind);
        }

        static void AnalyzeNotableOpcode(
            BurstAsmStats stats,
            string mnemonic,
            string baseMnemonic,
            IReadOnlyList<string> operands)
        {
            // these classes report explicit opcode evidence; unrecognized spellings remain visible in the histogram.
            if (baseMnemonic is "lfence" or "mfence" or "sfence" or "dmb" or "dsb" or "atomic.fence")
            {
                stats.FenceInstructionCount++;
                return;
            }

            if (baseMnemonic is "cpuid" or "serialize" or "iret" or "iretd" or "iretq" or "rsm" or "isb" or "sb")
            {
                stats.SerializingInstructionCount++;
                return;
            }

            if (baseMnemonic.IndexOf("gather", StringComparison.Ordinal) >= 0)
            {
                stats.GatherInstructionCount++;
                return;
            }

            if (baseMnemonic.IndexOf("scatter", StringComparison.Ordinal) >= 0)
            {
                stats.ScatterInstructionCount++;
                return;
            }

            if (baseMnemonic.Length > 0
                && baseMnemonic[0] is 'v' or 'f'
                && BurstInstructionParser.StartsWithAny(baseMnemonic, fusedMultiplyAddPrefixes))
            {
                stats.FusedMultiplyAddInstructionCount++;
                return;
            }

            if (baseMnemonic.IndexOf("sqrt", StringComparison.Ordinal) >= 0)
            {
                stats.SquareRootInstructionCount++;
                return;
            }

            if (baseMnemonic.IndexOf("div", StringComparison.Ordinal) >= 0)
            {
                stats.DivideInstructionCount++;
                return;
            }

            var hasMemoryOperand = false;
            foreach (var operand in operands)
                if (BurstInstructionParser.HasMemorySyntax(operand))
                {
                    hasMemoryOperand = true;
                    break;
                }

            if (mnemonic.StartsWith("lock ", StringComparison.Ordinal)
                || baseMnemonic == "xchg" && hasMemoryOperand
                || baseMnemonic.IndexOf(".atomic.", StringComparison.Ordinal) >= 0
                || baseMnemonic.StartsWith("atomic.", StringComparison.Ordinal)
                || BurstMemoryAnalyzer.IsArmReadModifyWrite(baseMnemonic)
                || baseMnemonic.Length > 0
                   && baseMnemonic[0] is 'l' or 's'
                   && BurstMemoryAnalyzer.StartsWithAtomicMemoryPrefix(baseMnemonic))
                stats.AtomicInstructionCount++;
        }

        static void AnalyzeCall(BurstAsmStats stats, string mnemonic, IReadOnlyList<string> operands)
        {
            if (!BurstInstructionParser.IsDirectCall(mnemonic, operands))
            {
                stats.IndirectCallCount++;
                return;
            }

            stats.DirectCallCount++;
            var target = BurstSymbolFormatter.CleanDiagnosticLine(
                BurstInstructionParser.CleanTransferTarget(operands[0])
            );
            if (target.Length == 0 || stats.DirectCallTargets.Contains(target))
                return;

            stats.DirectCallTargets.Add(target);
        }

        static bool IsXorMnemonic(string mnemonic) =>
            mnemonic is "xor" or "eor" or "veor" or "pxor" or "vpxor"
            or "xorps" or "xorpd" or "vxorps" or "vxorpd";

        static bool IsZeroingIdiom(IReadOnlyList<string> operands)
        {
            if (operands.Count < 2)
                return false;

            var left = operands.Count == 2 ? operands[0] : operands[^2];
            var right = operands[^1];
            return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)
                   && BurstInstructionParser.IsRegisterOperand(left);
        }

    }
}
