#nullable enable

namespace Conduit
{
    readonly struct BurstOutputTarget
    {
        internal readonly string Name;
        internal readonly string CompilerTarget;
        internal readonly string AsmKind;
        internal readonly BurstOutputKind OutputKind;
        internal string Dump => OutputKind switch
        {
            BurstOutputKind.Cil             => "IL",
            BurstOutputKind.OptimizedLlvmIr => "IROptimized",
            _                               => "Asm",
        };
        internal string DebugLevel => OutputKind == BurstOutputKind.Assembly ? "2" : "0";
        internal string DisplayName => OutputKind switch
        {
            BurstOutputKind.Cil             => "CIL",
            BurstOutputKind.OptimizedLlvmIr => "Optimized LLVM IR",
            _                               => "Assembly",
        };
        internal string CodeFence => OutputKind switch
        {
            BurstOutputKind.Cil             => "cil",
            BurstOutputKind.OptimizedLlvmIr => "llvm",
            _                               => "asm",
        };
        internal string FileExtension => OutputKind switch
        {
            BurstOutputKind.Cil             => ".il",
            BurstOutputKind.OptimizedLlvmIr => ".ll",
            _                               => ".txt",
        };

        internal BurstOutputTarget(string name, string compilerTarget, string asmKind)
        {
            Name = name;
            CompilerTarget = compilerTarget;
            AsmKind = asmKind;
            OutputKind = BurstOutputKind.Assembly;
        }

        internal BurstOutputTarget(
            string name,
            string compilerTarget,
            BurstOutputKind outputKind)
        {
            Name = name;
            CompilerTarget = compilerTarget;
            AsmKind = string.Empty;
            OutputKind = outputKind;
        }
    }

    enum BurstOutputKind : byte
    {
        Assembly,
        Cil,
        OptimizedLlvmIr,
    }
}
