#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class BurstAssemblyAnalyzer
    {
        static readonly Regex renderedSourceLocation = new(@"^\s*[#;]\s+(?<file>.+?):(?<line>\d+)(?:\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static BurstAsmStats Analyze(BurstTarget target, string disassembly, string cpu)
        {
            var lines = BurstOutputFormatter.SplitLines(disassembly);
            using var pooledBlocks = ConduitPool.GetPooledList<BurstAsmFunctionBlock>(out var blocks);
            BurstFunctionSelector.GetFunctionBlocks(lines, blocks);
            if (blocks.Count == 0)
                return AnalyzeLines(
                    lines,
                    0,
                    lines.Length,
                    BurstLoopAnalyzer.SupportsNativeLoopAnalysis(cpu, lines, 0, lines.Length)
                );

            var selected = BurstFunctionSelector.SelectMainBlock(target, blocks);
            using var pooledForwarders = ConduitPool.GetPooledList<string>(out var forwarders);
            // burst sometimes exposes the managed entry as a tiny thunk; follow only direct, scaffolding-only
            // forwarders; names such as $Invoke are useful evidence but do not establish which block holds the body.
            while (BurstFunctionSelector.TryGetForwardedBlock(lines, selected, blocks, out var forwarded))
            {
                var selectedName = selected.Label.Trim('"');
                var forwardedName = forwarded.Label.Trim('"');
                if (forwardedName == selectedName || forwarders.Contains(forwardedName))
                    break;

                forwarders.Add(selectedName);
                selected = forwarded;
            }

            var stats = AnalyzeLines(
                lines,
                selected.Start,
                selected.End,
                BurstLoopAnalyzer.SupportsNativeLoopAnalysis(cpu, lines, selected.Start, selected.End)
            );
            stats.AnalyzedFunction = selected.Label.Trim('"');
            stats.EntryForwarders.AddRange(forwarders);
            return stats;
        }

        static BurstAsmStats AnalyzeLines(string[] lines, int start, int end, bool analyzeLoops)
        {
            var stats = new BurstAsmStats();
            var labels = analyzeLoops
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : null;
            var labelsByInstruction = analyzeLoops
                ? new Dictionary<int, List<string>>()
                : null;
            var instructions = analyzeLoops
                ? new List<BurstAnalyzedInstruction>(end - start)
                : null;
            using var pooledOperands = ConduitPool.GetPooledList<string>(out var parsedOperands);
            var currentSource = string.Empty;
            for (int index = start; index < end; ++index)
            {
                var line = lines[index];
                if (TryReadRenderedSourceLocation(line, out var source))
                {
                    currentSource = source;
                    continue;
                }

                if (BurstInstructionParser.TryParseCodeLabel(line, out var label))
                {
                    if (!analyzeLoops)
                        continue;

                    labels![label] = stats.InstructionCount;
                    if (!labelsByInstruction!.TryGetValue(stats.InstructionCount, out var instructionLabels))
                    {
                        instructionLabels = new();
                        labelsByInstruction.Add(stats.InstructionCount, instructionLabels);
                    }
                    instructionLabels.Add(label);
                    continue;
                }

                if (!BurstInstructionParser.TryParseInstruction(line, out var mnemonic, out var operands))
                    continue;

                BurstInstructionParser.SplitOperands(operands, parsedOperands);
                stats.InstructionCount++;
                stats.InstructionCounts.TryGetValue(mnemonic, out var mnemonicCount);
                stats.InstructionCounts[mnemonic] = mnemonicCount + 1;
                var facts = BurstInstructionAnalyzer.AnalyzeInstruction(stats, mnemonic, operands, parsedOperands);
                var isConditionalBranch = BurstInstructionParser.IsConditionalBranch(mnemonic, parsedOperands);
                var isUnconditionalBranch = BurstInstructionParser.IsUnconditionalBranch(mnemonic);

                if (isConditionalBranch)
                    stats.ConditionalBranchCount++;
                else if (isUnconditionalBranch)
                    stats.UnconditionalBranchCount++;

                if (analyzeLoops)
                {
                    var branchTarget = string.Empty;
                    if ((isConditionalBranch || isUnconditionalBranch)
                        && BurstInstructionParser.TryGetDirectBranchTarget(parsedOperands, out var directBranchTarget))
                        branchTarget = BurstInstructionParser.CleanTransferTarget(directBranchTarget);

                    instructions!.Add(new(
                        mnemonic,
                        branchTarget,
                        currentSource,
                        facts,
                        isConditionalBranch,
                        isUnconditionalBranch
                    ));
                }

                if (currentSource.Length > 0)
                {
                    stats.MappedInstructionCount++;
                    if (!stats.SourceAttribution.TryGetValue(currentSource, out var sourceStats))
                    {
                        sourceStats = new(currentSource, stats.SourceAttribution.Count);
                        stats.SourceAttribution.Add(currentSource, sourceStats);
                    }
                    sourceStats.Add(facts, isConditionalBranch || isUnconditionalBranch);
                }
                else
                    stats.UnmappedInstructionCount++;
            }

            if (analyzeLoops)
                BurstLoopAnalyzer.AnalyzeNativeLoops(stats, instructions!, labels!, labelsByInstruction!);

            return stats;
        }

        static bool TryReadRenderedSourceLocation(string line, out string source)
        {
            source = string.Empty;
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.Length < 2 || trimmed[0] is not ('#' or ';') || !char.IsWhiteSpace(trimmed[1]))
                return false;

            var match = renderedSourceLocation.Match(line);
            if (!match.Success)
                return true;

            var file = Path.GetFileName(match.Groups["file"].Value.Replace('\\', '/'));
            source = $"{file}:{match.Groups["line"].Value}";
            return true;
        }
    }
}
