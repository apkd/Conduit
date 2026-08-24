#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class BurstSimdAnalyzer
    {
        static readonly string[] vectorLaneParts =
        {
            "shuf", "perm", "blend", "unpck", "pack", "insert", "extract",
            "replace", "splat", "swizzle", "pinsr", "pextr", "alignr",
        };
        static readonly string[] vectorLanePrefixes = { "zip", "uzp", "trn", "tbl", "tbx", "ext", "ins", "dup", "rev" };

        internal static BurstSimdRole ClassifySimd(string mnemonic, string operands, bool isZeroingXor)
        {
            var registers = BurstInstructionParser.ClassifyRegisters(operands);
            var hasXmm = (registers & BurstRegisterKinds.Xmm) != 0;
            var hasYmm = (registers & BurstRegisterKinds.Ymm) != 0;
            var hasZmm = (registers & BurstRegisterKinds.Zmm) != 0;
            var hasArmVector = (registers & BurstRegisterKinds.ArmVector) != 0;
            var hasArmScalar = (registers & BurstRegisterKinds.ArmScalar) != 0;
            var hasPackedMnemonic = BurstInstructionParser.IsPackedVectorMnemonic(mnemonic);
            var isVector = hasXmm || hasYmm || hasZmm || hasArmVector || hasArmScalar
                           || hasPackedMnemonic
                           || mnemonic is "vzeroupper" or "vzeroall";
            if (!isVector)
                return BurstSimdRole.None;
            if (isZeroingXor || mnemonic is "vzeroupper" or "vzeroall")
                return BurstSimdRole.Setup;
            if (IsVectorTransfer(mnemonic))
                return BurstSimdRole.Transfer;
            if (IsVectorLaneOperation(mnemonic))
                return BurstSimdRole.Lane;
            if (BurstInstructionParser.IsScalarSimdMnemonic(mnemonic) || hasArmScalar && !hasArmVector)
                return BurstSimdRole.ScalarCompute;
            if (hasXmm || hasYmm || hasZmm || hasArmVector || hasPackedMnemonic)
                return BurstSimdRole.PackedCompute;

            return BurstSimdRole.Other;
        }

        static bool IsVectorTransfer(string mnemonic) =>
            mnemonic.StartsWith("mov", StringComparison.Ordinal)
            || mnemonic.StartsWith("vmov", StringComparison.Ordinal)
            || mnemonic.StartsWith("vld", StringComparison.Ordinal)
            || mnemonic.StartsWith("vst", StringComparison.Ordinal)
            || mnemonic.IndexOf(".load", StringComparison.Ordinal) > 0
            || mnemonic.IndexOf(".store", StringComparison.Ordinal) > 0
            || BurstMemoryAnalyzer.IsLoadMnemonic(mnemonic)
            || BurstMemoryAnalyzer.IsStoreMnemonic(mnemonic)
            || mnemonic.IndexOf("broadcast", StringComparison.Ordinal) >= 0;

        static bool IsVectorLaneOperation(string mnemonic) =>
            ContainsAny(mnemonic, vectorLaneParts)
            || BurstInstructionParser.StartsWithAny(mnemonic, vectorLanePrefixes);

        static bool ContainsAny(string value, string[] parts)
        {
            foreach (var part in parts)
                if (value.IndexOf(part, StringComparison.Ordinal) >= 0)
                    return true;

            return false;
        }

        internal static void RecordPackedComputeWidth(BurstAsmStats stats, string operands)
        {
            var registers = BurstInstructionParser.ClassifyRegisters(operands);
            if ((registers & BurstRegisterKinds.ScalableArmVector) != 0)
            {
                stats.PackedComputeUsesScalableVectors = true;
                return;
            }

            var width = (registers & BurstRegisterKinds.Zmm) != 0 ? 512
                : (registers & BurstRegisterKinds.Ymm) != 0 ? 256
                : 128;
            stats.PackedComputeWidth = Math.Max(stats.PackedComputeWidth, width);
        }

    }
}
