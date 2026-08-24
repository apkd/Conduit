#nullable enable

using System;
using System.Text;

namespace Conduit
{
    static class BurstMemoryReportFormatter
    {
        internal static void AppendMemory(StringBuilder builder, BurstAsmStats stats)
        {
            var appendedAccess = false;
            Add(stats.LoadInstructionCount, "load", "loads");
            Add(stats.StoreInstructionCount, "store", "stores");
            Add(stats.ReadModifyWriteInstructionCount, "read-modify-write", "read-modify-write");
            Add(stats.OtherMemoryInstructionCount, "unclassified", "unclassified");
            if (appendedAccess)
            {
                var appendedAnnotation = false;
                if (stats.StackFrameMemoryInstructionCount > 0)
                    AppendAnnotation("stack/frame", stats.StackFrameMemoryInstructionCount);
                if (stats.ConstantPoolMemoryInstructionCount > 0)
                    AppendAnnotation("constant-pool", stats.ConstantPoolMemoryInstructionCount);
                builder.Append('\n');

                void AppendAnnotation(string name, int count)
                {
                    builder.Append(appendedAnnotation ? ", " : "; ")
                        .Append(name)
                        .Append(' ')
                        .Append(count);
                    appendedAnnotation = true;
                }
            }

            var appendedStack = false;
            if (stats.PushInstructionCount > 0)
                AppendStack("push", stats.PushInstructionCount);
            if (stats.PopInstructionCount > 0)
                AppendStack("pop", stats.PopInstructionCount);
            if (appendedStack)
                builder.Append('\n');
            if (stats.AddressGenerationInstructionCount > 0)
                builder.Append("- Address generation instructions: ")
                    .Append(stats.AddressGenerationInstructionCount)
                    .Append('\n');

            void Add(int count, string singular, string plural)
            {
                if (count <= 0)
                    return;

                builder.Append(appendedAccess ? ", " : "- Memory access instructions: ")
                    .Append(count)
                    .Append(' ')
                    .Append(count == 1 ? singular : plural);
                appendedAccess = true;
            }

            void AppendStack(string name, int count)
            {
                builder.Append(appendedStack ? ", " : "- Explicit stack operations: ")
                    .Append(name)
                    .Append(' ')
                    .Append(count);
                appendedStack = true;
            }
        }

        internal static void AppendIntegerIdioms(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.XorInstructionCount > 0)
            {
                var nonZeroing = stats.XorInstructionCount - stats.ZeroingXorInstructionCount;
                builder.Append($"- XOR instructions: {stats.XorInstructionCount}");
                if (stats.ZeroingXorInstructionCount > 0)
                    builder.Append($"; zeroing {stats.ZeroingXorInstructionCount}");
                if (nonZeroing > 0)
                    builder.Append($"; non-zeroing {nonZeroing}");
                builder.Append('\n');
            }

            if (stats.NumericMovabsCount == 0 && stats.SymbolMovabsCount == 0)
                return;

            var appended = false;
            if (stats.NumericMovabsCount > 0)
                Append("numeric constants", stats.NumericMovabsCount);
            if (stats.SymbolMovabsCount > 0)
                Append("symbol addresses", stats.SymbolMovabsCount);
            builder.Append('\n');

            void Append(string name, int count)
            {
                builder.Append(appended ? ", " : "- `movabs` materialization: ")
                    .Append(name)
                    .Append(' ')
                    .Append(count);
                appended = true;
            }
        }

        internal static void AppendNotableOpcodes(StringBuilder builder, BurstAsmStats stats)
        {
            var appended = false;
            Add(stats.DivideInstructionCount, "divide");
            Add(stats.SquareRootInstructionCount, "square root");
            Add(stats.FusedMultiplyAddInstructionCount, "FMA");
            Add(stats.GatherInstructionCount, "gather");
            Add(stats.ScatterInstructionCount, "scatter");
            Add(stats.AtomicInstructionCount, "atomic");
            Add(stats.FenceInstructionCount, "fence");
            Add(stats.SerializingInstructionCount, "serializing");
            if (!appended)
                return;

            builder.Append('\n');

            void Add(int count, string name)
            {
                if (count <= 0)
                    return;

                builder.Append(appended ? ", " : "- Notable opcode classes: ")
                    .Append(name)
                    .Append(' ')
                    .Append(count);
                appended = true;
            }
        }
    }
}
