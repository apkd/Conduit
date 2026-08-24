#nullable enable

using System;
using System.Text;

namespace Conduit
{
    static class BurstLoopReportFormatter
    {
        const int MaxLoopRows = 16;

        internal static void AppendSummary(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.LoopAnalysisDiagnostic.Length > 0)
            {
                builder.Append($"- Loop analysis suppressed: {stats.LoopAnalysisDiagnostic}\n");
                return;
            }
            if (!stats.LoopAnalysisCompleted)
                return;
            if (stats.Loops.Count == 0)
            {
                builder.Append("- Natural loops: 0\n");
                return;
            }

            var backedges = stats.LoopBackedgeCount == 1 ? "1 backedge" : $"{stats.LoopBackedgeCount} backedges";
            builder.Append(
                $"- Natural loops: {stats.Loops.Count}; {backedges}; max depth {stats.LoopMaxDepth}; " +
                $"{stats.LoopRegionInstructionCount}/{stats.InstructionCount} instr in loop regions\n"
            );
        }

        internal static void AppendDetails(StringBuilder builder, BurstAsmStats stats)
        {
            if (!stats.LoopAnalysisCompleted || stats.Loops.Count == 0)
                return;

            using var pooledRows = ConduitPool.GetPooledList<(BurstLoopStats Loop, int Indent)>(out var rows);
            foreach (var loop in stats.Loops)
                if (loop.Parent == null)
                    Visit(loop, 0);

            builder.Append("\n# Loops\n\n");
            var count = Math.Min(rows.Count, MaxLoopRows);
            for (var index = 0; index < count; ++index)
            {
                var (loop, indent) = rows[index];
                builder.Append(' ', indent * 2);
                builder.Append($"- `L{loop.Id}` `{loop.Header}`");
                if (loop.Source.Length > 0)
                    builder.Append($" @ `{loop.Source}`");
                builder.Append($": {loop.ExclusiveInstructionCount} instr");
                if (loop.NestedInstructionCount > 0)
                    builder.Append($" + {loop.NestedInstructionCount} nested");

                builder.Append(" (");
                var appendedDetail = false;
                Add(loop.LoadCount, "loads");
                Add(loop.StoreCount, "stores");
                Add(loop.PackedComputeCount, "packed compute");
                Add(loop.BranchCount, "branches");
                Add(loop.DirectCallCount, "direct calls");
                Add(loop.IndirectCallCount, "indirect calls");
                if (appendedDetail)
                    builder.Append("; ");
                builder.Append($"exits {loop.ExitCount}");
                if (loop.BackedgeCount > 1)
                    builder.Append($"; backedges {loop.BackedgeCount}");
                builder.Append(")\n");

                void Add(int value, string name)
                {
                    if (value <= 0)
                        return;

                    if (appendedDetail)
                        builder.Append(", ");
                    builder.Append(name)
                        .Append(' ')
                        .Append(value);
                    appendedDetail = true;
                }
            }

            if (rows.Count > count)
                builder.Append($"- {rows.Count - count} more loops omitted.\n");

            void Visit(BurstLoopStats loop, int indent)
            {
                rows.Add((loop, indent));
                foreach (var child in loop.Children)
                    Visit(child, indent + 1);
            }
        }
    }
}
