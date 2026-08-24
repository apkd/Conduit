#nullable enable

using System;
using System.Text;

namespace Conduit
{
    static class BurstReportFormatter
    {
        const int MaxDirectCalleeDetails = 5;

        internal static string FormatReport(BurstTarget target, BurstAsmStats stats)
        {
            var function = ReportFunction(target, stats);
            return FormatStats(stats, function);
        }

        internal static string FormatRawReport(BurstTarget target, BurstOutputTarget outputTarget, BurstAsmStats stats)
        {
            var function = BurstSymbolFormatter.CleanDisplayName(target.DisplayName);
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append($"**Function:** `{function}`");
            if (outputTarget.OutputKind == BurstOutputKind.OptimizedLlvmIr)
            {
                builder.Append("\n\n");
                AppendCompilation(builder, stats.Context, false);
                BurstOptimizationReportFormatter.AppendRemarks(builder, stats, function);
            }

            return builder.ToTrimmedString();
        }

        static string ReportFunction(BurstTarget target, BurstAsmStats stats) =>
            stats.EntryForwarders.Count > 0 ? stats.EntryForwarders[0]
            : stats.AnalyzedFunction.Length > 0 ? stats.AnalyzedFunction
            : BurstSymbolFormatter.CleanDisplayName(target.DisplayName);

        static string FormatStats(BurstAsmStats stats, string function)
        {
            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.Append("# Summary\n\n");
            builder.Append($"**Function:** `{function}`\n\n");
            AppendCompilation(builder, stats.Context, true);
            builder.Append($"- Instructions: {stats.InstructionCount}\n");
            AppendControlFlow(builder, stats);
            BurstLoopReportFormatter.AppendSummary(builder, stats);
            AppendCalls(builder, stats);
            AppendSimd(builder, stats);
            BurstMemoryReportFormatter.AppendMemory(builder, stats);
            BurstMemoryReportFormatter.AppendIntegerIdioms(builder, stats);
            BurstMemoryReportFormatter.AppendNotableOpcodes(builder, stats);

            BurstOptimizationReportFormatter.AppendMnemonicHistogram(builder, stats);
            BurstLoopReportFormatter.AppendDetails(builder, stats);
            BurstOptimizationReportFormatter.AppendSourceAttribution(builder, stats);
            BurstOptimizationReportFormatter.AppendNotes(builder, stats);
            BurstOptimizationReportFormatter.AppendRemarks(builder, stats, function);
            return builder.ToTrimmedString();
        }

        static void AppendCompilation(
            StringBuilder builder,
            BurstCompilationContext context,
            bool includeArchitecture)
        {
            if (context.IsEmpty)
                return;

            builder.Append("**Compilation:** ");
            builder.Append(includeArchitecture
                ? $"`{context.Cpu}/{context.CompilerTarget}` · "
                : "target `Compiler default` · ");
            builder.Append($"`{context.Optimization}` · ");
            builder.Append($"floats `{context.FloatMode}/{context.FloatPrecision}` · ");
            builder.Append($"safety checks `{context.SafetyChecks}`\n\n");
        }

        static void AppendCalls(StringBuilder builder, BurstAsmStats stats)
        {
            builder.Append($"- Calls: direct {stats.DirectCallCount}");
            if (stats.DirectCallTargets.Count > 0)
            {
                builder.Append(" (");
                var count = Math.Min(stats.DirectCallTargets.Count, MaxDirectCalleeDetails);
                for (int index = 0; index < count; ++index)
                {
                    if (index > 0)
                        builder.Append(", ");
                    builder.Append($"`{stats.DirectCallTargets[index]}`");
                }
                if (stats.DirectCallTargets.Count > count)
                    builder.Append($"; {stats.DirectCallTargets.Count - count} more");
                builder.Append(')');
            }
            builder.Append($", indirect {stats.IndirectCallCount}\n");
        }

        static void AppendControlFlow(StringBuilder builder, BurstAsmStats stats)
        {
            var appended = false;
            Add(stats.ConditionalBranchCount, "conditional branch", "conditional branches");
            Add(stats.UnconditionalBranchCount, "jump", "jumps");
            Add(stats.ReturnCount, "return", "returns");
            if (appended)
                builder.Append('\n');

            void Add(int count, string singular, string plural)
            {
                if (count <= 0)
                    return;

                builder.Append(appended ? ", " : "- Control flow: ")
                    .Append(count)
                    .Append(' ')
                    .Append(count == 1 ? singular : plural);
                appended = true;
            }
        }

        static void AppendSimd(StringBuilder builder, BurstAsmStats stats)
        {
            var appended = false;
            Add(stats.SimdPackedComputeInstructionCount, "packed compute");
            Add(stats.SimdScalarComputeInstructionCount, "scalar compute");
            Add(stats.SimdTransferInstructionCount, "transfer");
            Add(stats.SimdLaneInstructionCount, "lane/shuffle");
            Add(stats.SimdSetupInstructionCount, "setup/control");
            Add(stats.SimdOtherInstructionCount, "unclassified");
            if (!appended)
                return;

            if (stats.PackedComputeUsesScalableVectors)
                builder.Append("; widest packed compute scalable (SVE)");
            else if (stats.PackedComputeWidth > 0)
                builder.Append("; widest packed compute ")
                    .Append(stats.PackedComputeWidth)
                    .Append("-bit");
            builder.Append('\n');

            void Add(int count, string name)
            {
                if (count <= 0)
                    return;

                builder.Append(appended ? ", " : "- SIMD: ")
                    .Append(name)
                    .Append(' ')
                    .Append(count);
                appended = true;
            }
        }
    }
}
