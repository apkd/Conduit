#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace Conduit
{
    static class BurstOptimizationReportFormatter
    {
        const int MaxTopInstructions = 20;
        const int MaxSourceAttributionRows = 16;

        internal static void AppendNotes(StringBuilder builder, BurstAsmStats stats)
        {
            var movementOnly = stats.SimdPackedComputeInstructionCount == 0
                               && stats.SimdScalarComputeInstructionCount == 0
                               && stats.SimdOtherInstructionCount == 0
                               && stats.SimdTransferInstructionCount + stats.SimdLaneInstructionCount > 0;
            var fastCompilation = stats.Context.Optimization == "Fast compilation";
            if (!movementOnly && !fastCompilation)
                return;

            builder.Append("\n## Notes\n\n");
            if (movementOnly)
                builder.Append("- Vector registers are used only for transfers or lane manipulation; packed computation was not established.\n");
            if (fastCompilation)
                builder.Append("- Fast compilation limits Burst vectorization, inlining, and loop optimization.\n");
        }

        internal static void AppendRemarks(StringBuilder builder, BurstAsmStats stats, string function)
        {
            if (stats.OptimizationRemarks.Count > 0)
            {
                if (builder.Length == 0 || builder[^1] != '\n')
                    builder.Append('\n');
                if (builder.Length < 2 || builder[^2] != '\n')
                    builder.Append('\n');
                builder.Append("## Compiler optimization remarks\n\n");
                foreach (var remark in stats.OptimizationRemarks)
                {
                    builder.Append($"- `{remark.Type}`");
                    if (remark.Pass.Length > 0)
                    {
                        builder.Append($" · `{remark.Pass}");
                        if (remark.Reason.Length > 0)
                            builder.Append($"/{remark.Reason}");
                        builder.Append('`');
                    }
                    if (remark.Function.Length > 0
                        && BurstFunctionSelector.NormalizeAsmText(remark.Function)
                        != BurstFunctionSelector.NormalizeAsmText(function))
                        builder.Append($" · function `{remark.Function}`");
                    if (remark.Source.Length > 0)
                        builder.Append($" · `{remark.Source}`");
                    builder.Append($" — {remark.Message}\n");
                }
            }

            if (stats.RemarksError.Length > 0)
                builder.Append($"\n*Compiler optimization remarks could not be retrieved: {stats.RemarksError}*\n");
        }

        internal static void AppendSourceAttribution(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.MappedInstructionCount == 0)
                return;

            using var pooledRows = ConduitPool.GetPooledList<BurstSourceStats>(out var rows);
            foreach (var row in stats.SourceAttribution.Values)
                rows.Add(row);
            rows.Sort(static (left, right) =>
            {
                var count = right.InstructionCount.CompareTo(left.InstructionCount);
                return count != 0 ? count : left.Ordinal.CompareTo(right.Ordinal);
            });

            builder.Append("\n# Source attribution\n\n");
            builder.Append($"- Coverage: {stats.MappedInstructionCount}/{stats.InstructionCount} instructions mapped");
            if (stats.UnmappedInstructionCount > 0)
                builder.Append($"; {stats.UnmappedInstructionCount} unmapped/compiler-generated");
            builder.Append('\n');

            var count = Math.Min(rows.Count, MaxSourceAttributionRows);
            for (int index = 0; index < count; ++index)
            {
                var row = rows[index];
                builder.Append($"- `{row.Source}`: {row.InstructionCount} instr");
                var appendedDetail = false;
                Add(row.LoadCount, "loads");
                Add(row.StoreCount, "stores");
                Add(row.PackedComputeCount, "packed compute");
                Add(row.BranchCount, "branches");
                Add(row.CallCount, "calls");
                if (appendedDetail)
                    builder.Append(')');
                builder.Append('\n');

                void Add(int value, string name)
                {
                    if (value <= 0)
                        return;

                    builder.Append(appendedDetail ? ", " : " (")
                        .Append(name)
                        .Append(' ')
                        .Append(value);
                    appendedDetail = true;
                }
            }

            if (rows.Count > count)
                builder.Append($"- {rows.Count - count} more source mappings omitted.\n");
        }

        internal static void AppendMnemonicHistogram(StringBuilder builder, BurstAsmStats stats)
        {
            if (stats.InstructionCounts.Count == 0)
                return;

            using var pooledEntries = ConduitPool.GetPooledList<KeyValuePair<string, int>>(out var entries);
            foreach (var entry in stats.InstructionCounts)
                entries.Add(entry);
            entries.Sort(static (left, right) =>
            {
                var count = right.Value.CompareTo(left.Value);
                return count != 0
                    ? count
                    : string.Compare(left.Key, right.Key, StringComparison.Ordinal);
            });

            builder.Append("- Top instructions: ");
            var count = Math.Min(entries.Count, MaxTopInstructions);
            for (int index = 0; index < count; ++index)
            {
                if (index > 0)
                    builder.Append(", ");
                builder.Append(entries[index].Key)
                    .Append('=')
                    .Append(entries[index].Value);
            }
            builder.Append('\n');
        }
    }
}
