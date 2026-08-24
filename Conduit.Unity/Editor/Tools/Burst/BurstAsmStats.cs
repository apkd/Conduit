#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    sealed class BurstAsmStats
    {
        internal readonly Dictionary<string, int> InstructionCounts = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, BurstSourceStats> SourceAttribution = new(StringComparer.Ordinal);
        internal readonly List<string> DirectCallTargets = new();
        internal readonly List<string> EntryForwarders = new();
        internal readonly List<BurstOptimizationRemark> OptimizationRemarks = new();
        internal readonly List<BurstLoopStats> Loops = new();
        internal BurstCompilationContext Context;
        internal string AnalyzedFunction = string.Empty;
        internal string RemarksError = string.Empty;
        internal int InstructionCount;
        internal int MappedInstructionCount;
        internal int UnmappedInstructionCount;
        internal int ConditionalBranchCount;
        internal int UnconditionalBranchCount;
        internal bool LoopAnalysisCompleted;
        internal string LoopAnalysisDiagnostic = string.Empty;
        internal int LoopBackedgeCount;
        internal int LoopMaxDepth;
        internal int LoopRegionInstructionCount;
        internal int DirectCallCount;
        internal int IndirectCallCount;
        internal int ReturnCount;
        internal int LoadInstructionCount;
        internal int StoreInstructionCount;
        internal int ReadModifyWriteInstructionCount;
        internal int OtherMemoryInstructionCount;
        internal int StackFrameMemoryInstructionCount;
        internal int ConstantPoolMemoryInstructionCount;
        internal int AddressGenerationInstructionCount;
        internal int PushInstructionCount;
        internal int PopInstructionCount;
        internal int SimdTransferInstructionCount;
        internal int SimdLaneInstructionCount;
        internal int SimdScalarComputeInstructionCount;
        internal int SimdPackedComputeInstructionCount;
        internal int SimdSetupInstructionCount;
        internal int SimdOtherInstructionCount;
        internal int PackedComputeWidth;
        internal bool PackedComputeUsesScalableVectors;
        internal int XorInstructionCount;
        internal int ZeroingXorInstructionCount;
        internal int NumericMovabsCount;
        internal int SymbolMovabsCount;
        internal int DivideInstructionCount;
        internal int SquareRootInstructionCount;
        internal int FusedMultiplyAddInstructionCount;
        internal int GatherInstructionCount;
        internal int ScatterInstructionCount;
        internal int AtomicInstructionCount;
        internal int FenceInstructionCount;
        internal int SerializingInstructionCount;
    }
}
