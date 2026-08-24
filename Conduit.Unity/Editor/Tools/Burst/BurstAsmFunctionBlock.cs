#nullable enable

namespace Conduit
{
    readonly struct BurstAsmFunctionBlock
    {
        internal readonly string Label;
        internal readonly int Start;
        internal readonly int End;
        internal readonly int InstructionCount;

        internal BurstAsmFunctionBlock(string label, int start, int end, int instructionCount)
        {
            Label = label;
            Start = start;
            End = end;
            InstructionCount = instructionCount;
        }
    }
}
