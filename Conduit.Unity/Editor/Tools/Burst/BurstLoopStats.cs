#nullable enable

using System.Collections.Generic;

namespace Conduit
{
    sealed class BurstLoopStats
    {
        internal readonly int HeaderBlock;
        internal readonly HashSet<int> Blocks;
        internal readonly int BackedgeCount;
        internal readonly List<BurstLoopStats> Children = new();
        internal BurstLoopStats? Parent;
        internal string Header = string.Empty;
        internal string Source = string.Empty;
        internal int Id;
        internal int Depth;
        internal int InclusiveInstructionCount;
        internal int ExclusiveInstructionCount;
        internal int NestedInstructionCount;
        internal int ExitCount;
        internal int LoadCount;
        internal int StoreCount;
        internal int PackedComputeCount;
        internal int BranchCount;
        internal int DirectCallCount;
        internal int IndirectCallCount;

        internal BurstLoopStats(int headerBlock, HashSet<int> blocks, int backedgeCount)
        {
            HeaderBlock = headerBlock;
            Blocks = blocks;
            BackedgeCount = backedgeCount;
        }

        internal void Add(BurstAnalyzedInstruction instruction)
        {
            ExclusiveInstructionCount++;
            if (instruction.Facts.MemoryAccess is BurstMemoryAccessKind.Load or BurstMemoryAccessKind.ReadModifyWrite)
                LoadCount++;
            if (instruction.Facts.MemoryAccess is BurstMemoryAccessKind.Store or BurstMemoryAccessKind.ReadModifyWrite)
                StoreCount++;
            if (instruction.Facts.SimdRole == BurstSimdRole.PackedCompute)
                PackedComputeCount++;
            if (instruction.IsConditionalBranch || instruction.IsUnconditionalBranch)
                BranchCount++;
            if (instruction.Facts.CallKind == BurstCallKind.Direct)
                DirectCallCount++;
            else if (instruction.Facts.CallKind == BurstCallKind.Indirect)
                IndirectCallCount++;
        }
    }
}
