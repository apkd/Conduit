#nullable enable

namespace Conduit
{
    sealed class BurstSourceStats
    {
        internal readonly string Source;
        internal readonly int Ordinal;
        internal int InstructionCount;
        internal int LoadCount;
        internal int StoreCount;
        internal int PackedComputeCount;
        internal int BranchCount;
        internal int CallCount;

        internal BurstSourceStats(string source, int ordinal)
        {
            Source = source;
            Ordinal = ordinal;
        }

        internal void Add(BurstInstructionFacts facts, bool isBranch)
        {
            InstructionCount++;
            if (facts.MemoryAccess is BurstMemoryAccessKind.Load or BurstMemoryAccessKind.ReadModifyWrite)
                LoadCount++;
            if (facts.MemoryAccess is BurstMemoryAccessKind.Store or BurstMemoryAccessKind.ReadModifyWrite)
                StoreCount++;
            if (facts.SimdRole == BurstSimdRole.PackedCompute)
                PackedComputeCount++;
            if (isBranch)
                BranchCount++;
            if (facts.CallKind != BurstCallKind.None)
                CallCount++;
        }
    }
}
