#nullable enable

using System.Collections.Generic;

namespace Conduit
{
    sealed class BurstControlFlowBlock
    {
        internal readonly int Index;
        internal readonly int Start;
        internal readonly int End;
        internal readonly List<int> Successors = new();
        internal readonly List<int> Predecessors = new();
        internal string IncompleteReason = string.Empty;
        internal int ExitPathCount;

        internal BurstControlFlowBlock(int index, int start, int end)
        {
            Index = index;
            Start = start;
            End = end;
        }
    }
}
