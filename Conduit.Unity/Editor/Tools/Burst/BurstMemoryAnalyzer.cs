#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static class BurstMemoryAnalyzer
    {
        static readonly string[] atomicMemoryPrefixes = { "ldar", "ldaxr", "ldxr", "stlr", "stlxr", "stxr" };
        internal static BurstMemoryAccessKind ClassifyMemoryAccess(
            string mnemonic,
            IReadOnlyList<string> operands)
        {
            if (IsAddressGeneration(mnemonic))
                return BurstMemoryAccessKind.None;

            var memoryIndex = -1;
            for (int index = 0, n = operands.Count; index < n; ++index)
            {
                if (!BurstInstructionParser.HasMemorySyntax(operands[index]))
                    continue;

                memoryIndex = index;
                break;
            }

            // arm and wasm encode direction in the mnemonic; x86 needs destination-position semantics.
            if (IsArmReadModifyWrite(mnemonic))
                return BurstMemoryAccessKind.ReadModifyWrite;
            if (IsLoadMnemonic(mnemonic))
                return BurstMemoryAccessKind.Load;
            if (IsStoreMnemonic(mnemonic))
                return BurstMemoryAccessKind.Store;
            if (memoryIndex < 0)
                return BurstMemoryAccessKind.None;
            if (memoryIndex > 0)
                return BurstMemoryAccessKind.Load;

            if (IsReadOnlyMemoryDestination(mnemonic))
                return BurstMemoryAccessKind.Load;
            if (IsWriteOnlyMemoryDestination(mnemonic))
                return BurstMemoryAccessKind.Store;
            if (IsReadModifyWriteMnemonic(mnemonic))
                return BurstMemoryAccessKind.ReadModifyWrite;

            return BurstMemoryAccessKind.Other;
        }

        internal static bool IsAddressGeneration(string mnemonic) =>
            mnemonic is "lea" or "adr" or "adrp";

        internal static bool IsArmReadModifyWrite(string mnemonic) =>
            mnemonic.Length > 0
            && mnemonic[0] switch
            {
                'l' => mnemonic.StartsWith("ldadd", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldclr", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldeor", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldset", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldsmax", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldsmin", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldumax", StringComparison.Ordinal)
                       || mnemonic.StartsWith("ldumin", StringComparison.Ordinal),
                's' => mnemonic.StartsWith("swp", StringComparison.Ordinal),
                'c' => mnemonic.StartsWith("cas", StringComparison.Ordinal),
                _ => false,
            };

        internal static bool IsLoadMnemonic(string mnemonic) =>
            mnemonic.IndexOf(".load", StringComparison.Ordinal) > 0
            || mnemonic.Length > 0
               && mnemonic[0] switch
               {
                   'l' => mnemonic.StartsWith("ldr", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ldp", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ld1", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ld2", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ld3", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ld4", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ldar", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ldax", StringComparison.Ordinal)
                          || mnemonic.StartsWith("ldxr", StringComparison.Ordinal),
                   'f' => mnemonic is "fld" or "fld1" or "fldl2e" or "fldl2t" or "fldlg2" or "fldln2" or "fldpi" or "fldz",
                   _ => false,
               };

        internal static bool IsStoreMnemonic(string mnemonic) =>
            mnemonic.IndexOf(".store", StringComparison.Ordinal) > 0
            || mnemonic.Length > 0
               && mnemonic[0] == 's'
               && (mnemonic.StartsWith("str", StringComparison.Ordinal)
                   || mnemonic.StartsWith("stp", StringComparison.Ordinal)
                   || mnemonic.StartsWith("st1", StringComparison.Ordinal)
                   || mnemonic.StartsWith("st2", StringComparison.Ordinal)
                   || mnemonic.StartsWith("st3", StringComparison.Ordinal)
                   || mnemonic.StartsWith("st4", StringComparison.Ordinal)
                   || mnemonic.StartsWith("stlr", StringComparison.Ordinal)
                   || mnemonic.StartsWith("stlx", StringComparison.Ordinal)
                   || mnemonic.StartsWith("stxr", StringComparison.Ordinal));

        static bool IsReadOnlyMemoryDestination(string mnemonic) =>
            mnemonic is "cmp" or "test" or "bt" or "call" or "jmp" or "push"
            || mnemonic.StartsWith("prefetch", StringComparison.Ordinal)
            || mnemonic.StartsWith("mul", StringComparison.Ordinal)
            || mnemonic is "imul" or "div" or "idiv";

        static bool IsWriteOnlyMemoryDestination(string mnemonic) =>
            mnemonic is "pop" or "fstp" or "fistp" or "stmxcsr"
            || mnemonic.StartsWith("mov", StringComparison.Ordinal)
            || mnemonic.StartsWith("vmov", StringComparison.Ordinal)
            || mnemonic.StartsWith("set", StringComparison.Ordinal);

        static bool IsReadModifyWriteMnemonic(string mnemonic) =>
            mnemonic is "add" or "adc" or "sub" or "sbb" or "and" or "or" or "xor"
            or "inc" or "dec" or "neg" or "not" or "xchg" or "cmpxchg";

        internal static bool StartsWithAtomicMemoryPrefix(string mnemonic)
        {
            foreach (var prefix in atomicMemoryPrefixes)
                if (mnemonic.StartsWith(prefix, StringComparison.Ordinal))
                    return true;

            return false;
        }

    }
}
