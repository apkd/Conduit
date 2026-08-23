#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace Conduit
{
    static partial class view_burst_asm
    {
        const int MaxLoopRows = 16;

        static bool SupportsNativeLoopAnalysis(string cpu, string[] lines, int start, int end)
        {
            var isWasm = string.Equals(cpu, "wasm32", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(cpu))
            {
                for (var index = start; index < end; ++index)
                {
                    if (IsWasmInstruction(lines[index]))
                    {
                        isWasm = true;
                        break;
                    }
                }
            }

            if (isWasm)
            {
                // TODO: reconstruct Wasm CFGs from structured block nesting and branch depths before enabling loop analysis.
                return false;
            }

            return string.IsNullOrEmpty(cpu)
                   || string.Equals(cpu, "x86", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(cpu, "armv8", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(cpu, "armv9", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsWasmInstruction(string line)
        {
            var text = line.AsSpan().Trim();
            if (text.Length == 0 || text[^1] == ':' || text[0] is '#' or ';' or '.'
                || text.StartsWith("//", StringComparison.Ordinal))
                return false;

            var tokenEnd = ReadTokenEnd(text, 0);
            if (tokenEnd == 0)
                return false;

            var mnemonic = text[..tokenEnd];
            return mnemonic.Equals("block", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.Equals("else", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.Equals("end", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.Equals("end_function", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.Equals("br_if", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.Equals("call_indirect", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.StartsWith("i32.", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.StartsWith("i64.", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.StartsWith("f32.", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.StartsWith("f64.", StringComparison.OrdinalIgnoreCase)
                   || mnemonic.StartsWith("v128.", StringComparison.OrdinalIgnoreCase);
        }

        static void AnalyzeNativeLoops(
            BurstAsmStats stats,
            IReadOnlyList<BurstAnalyzedInstruction> instructions,
            IReadOnlyDictionary<string, int> labels,
            IReadOnlyDictionary<int, List<string>> labelsByInstruction)
        {
            if (instructions.Count == 0)
            {
                stats.LoopAnalysisCompleted = true;
                return;
            }

            var boundaries = new HashSet<int> { 0 };
            for (var index = 0; index < instructions.Count; ++index)
            {
                var instruction = instructions[index];
                if ((instruction.IsConditionalBranch || instruction.IsUnconditionalBranch)
                    && TryGetBranchTarget(instruction, out var target)
                    && labels.TryGetValue(target, out var targetIndex)
                    && targetIndex < instructions.Count)
                    boundaries.Add(targetIndex);

                if ((instruction.IsConditionalBranch
                     || instruction.IsUnconditionalBranch
                     || IsReturn(instruction.Mnemonic)
                     || IsTrap(instruction.Mnemonic))
                    && index + 1 < instructions.Count)
                    boundaries.Add(index + 1);
            }

            var starts = new List<int>(boundaries);
            starts.Sort();
            var blocks = new List<BurstControlFlowBlock>(starts.Count);
            var instructionBlocks = new int[instructions.Count];
            for (var index = 0; index < starts.Count; ++index)
            {
                var end = index + 1 < starts.Count ? starts[index + 1] : instructions.Count;
                var block = new BurstControlFlowBlock(index, starts[index], end);
                blocks.Add(block);
                for (var instructionIndex = block.Start; instructionIndex < block.End; ++instructionIndex)
                    instructionBlocks[instructionIndex] = index;
            }

            foreach (var block in blocks)
            {
                var instruction = instructions[block.End - 1];
                if (instruction.IsConditionalBranch)
                {
                    if (!TryGetBranchTarget(instruction, out var target))
                        block.IncompleteReason = "unresolved conditional branch";
                    else if (!labels.TryGetValue(target, out var targetIndex))
                        block.IncompleteReason = $"missing conditional target `{target}`";
                    else if (targetIndex < instructions.Count)
                        AddEdge(block.Index, instructionBlocks[targetIndex]);
                    else
                        block.ExitPathCount++;

                    if (block.Index + 1 < blocks.Count)
                        AddEdge(block.Index, block.Index + 1);
                    else
                        block.ExitPathCount++;
                    continue;
                }

                if (instruction.IsUnconditionalBranch)
                {
                    if (!TryGetBranchTarget(instruction, out var target))
                        block.IncompleteReason = "reachable indirect jump";
                    else if (labels.TryGetValue(target, out var targetIndex) && targetIndex < instructions.Count)
                        AddEdge(block.Index, instructionBlocks[targetIndex]);
                    else if (target.StartsWith(".L", StringComparison.Ordinal))
                        block.IncompleteReason = $"missing jump target `{target}`";
                    else
                        block.ExitPathCount++; // a direct jump outside the selected function is a tail exit
                    continue;
                }

                if (IsReturn(instruction.Mnemonic) || IsTrap(instruction.Mnemonic))
                {
                    block.ExitPathCount++;
                    continue;
                }

                if (block.Index + 1 < blocks.Count)
                    AddEdge(block.Index, block.Index + 1);
                else
                    block.ExitPathCount++;
            }

            var reachable = new bool[blocks.Count];
            var pending = new Queue<int>();
            reachable[0] = true;
            pending.Enqueue(0);
            while (pending.Count > 0)
            {
                var blockIndex = pending.Dequeue();
                foreach (var successor in blocks[blockIndex].Successors)
                {
                    if (reachable[successor])
                        continue;

                    reachable[successor] = true;
                    pending.Enqueue(successor);
                }
            }

            foreach (var block in blocks)
            {
                if (!reachable[block.Index] || block.IncompleteReason.Length == 0)
                    continue;

                stats.LoopAnalysisDiagnostic =
                    $"{block.IncompleteReason} at `{BlockLabel(block, labelsByInstruction)}`.";
                return;
            }

            // dominance proves loop backedges; dense bitsets avoid a hash table per control-flow block
            var dominatorWordCount = (blocks.Count + 63) / 64;
            var reachableBlocks = new ulong[dominatorWordCount];
            for (var index = 0; index < reachable.Length; ++index)
                if (reachable[index])
                    AddBit(reachableBlocks, index);

            var dominators = new ulong[blocks.Count][];
            foreach (var block in blocks)
            {
                var values = new ulong[dominatorWordCount];
                if (block.Index == 0)
                    AddBit(values, 0);
                else if (reachable[block.Index])
                    Array.Copy(reachableBlocks, values, dominatorWordCount);

                dominators[block.Index] = values;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var blockIndex = 1; blockIndex < blocks.Count; ++blockIndex)
                {
                    if (!reachable[blockIndex])
                        continue;

                    ulong[]? next = null;
                    foreach (var predecessor in blocks[blockIndex].Predecessors)
                    {
                        if (!reachable[predecessor])
                            continue;

                        if (next == null)
                            next = (ulong[])dominators[predecessor].Clone();
                        else
                            IntersectBits(next, dominators[predecessor]);
                    }

                    next ??= new ulong[dominatorWordCount];
                    AddBit(next, blockIndex);
                    if (BitsEqual(next, dominators[blockIndex]))
                        continue;

                    dominators[blockIndex] = next;
                    changed = true;
                }
            }

            var latchesByHeader = new Dictionary<int, List<int>>();
            foreach (var block in blocks)
            {
                if (!reachable[block.Index])
                    continue;

                foreach (var successor in block.Successors)
                {
                    if (!ContainsBit(dominators[block.Index], successor))
                        continue;
                    if (!latchesByHeader.TryGetValue(successor, out var latches))
                    {
                        latches = new();
                        latchesByHeader.Add(successor, latches);
                    }
                    latches.Add(block.Index);
                }
            }

            var loops = new List<BurstLoopStats>(latchesByHeader.Count);
            foreach (var pair in latchesByHeader)
            {
                var members = new HashSet<int> { pair.Key };
                var predecessors = new Stack<int>();
                foreach (var latch in pair.Value)
                    if (members.Add(latch))
                        predecessors.Push(latch);

                while (predecessors.Count > 0)
                {
                    var blockIndex = predecessors.Pop();
                    foreach (var predecessor in blocks[blockIndex].Predecessors)
                    {
                        if (!reachable[predecessor] || !members.Add(predecessor) || predecessor == pair.Key)
                            continue;
                        predecessors.Push(predecessor);
                    }
                }

                loops.Add(new(pair.Key, members, pair.Value.Count));
            }
            loops.Sort((left, right) => blocks[left.HeaderBlock].Start.CompareTo(blocks[right.HeaderBlock].Start));

            for (var leftIndex = 0; leftIndex < loops.Count; ++leftIndex)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < loops.Count; ++rightIndex)
                {
                    var left = loops[leftIndex];
                    var right = loops[rightIndex];
                    if (!left.Blocks.Overlaps(right.Blocks))
                        continue;

                    var leftInsideRight = left.Blocks.Count < right.Blocks.Count && left.Blocks.IsSubsetOf(right.Blocks);
                    var rightInsideLeft = right.Blocks.Count < left.Blocks.Count && right.Blocks.IsSubsetOf(left.Blocks);
                    if (leftInsideRight || rightInsideLeft)
                        continue;

                    stats.LoopAnalysisDiagnostic =
                        $"natural loop regions overlap without nesting at " +
                        $"`{BlockLabel(blocks[left.HeaderBlock], labelsByInstruction)}` and " +
                        $"`{BlockLabel(blocks[right.HeaderBlock], labelsByInstruction)}`.";
                    return;
                }
            }

            foreach (var loop in loops)
            {
                foreach (var candidate in loops)
                {
                    if (ReferenceEquals(loop, candidate)
                        || loop.Blocks.Count >= candidate.Blocks.Count
                        || !loop.Blocks.IsSubsetOf(candidate.Blocks))
                        continue;
                    if (loop.Parent == null || candidate.Blocks.Count < loop.Parent.Blocks.Count)
                        loop.Parent = candidate;
                }
            }

            foreach (var loop in loops)
                loop.Parent?.Children.Add(loop);

            foreach (var loop in loops)
                loop.Depth = Depth(loop);

            for (var index = 0; index < loops.Count; ++index)
            {
                var loop = loops[index];
                loop.Id = index + 1;
                loop.Header = BlockLabel(blocks[loop.HeaderBlock], labelsByInstruction);
                loop.Source = FindSource(loop, blocks, instructions);

                using var pooledNestedBlocks = ConduitUtility.GetPooledSet<int>(out var nestedBlocks);
                foreach (var child in loop.Children)
                    nestedBlocks.UnionWith(child.Blocks);

                foreach (var blockIndex in loop.Blocks)
                {
                    var block = blocks[blockIndex];
                    loop.InclusiveInstructionCount += block.End - block.Start;
                    foreach (var successor in block.Successors)
                        if (!loop.Blocks.Contains(successor))
                            loop.ExitCount++;
                    loop.ExitCount += block.ExitPathCount;

                    if (nestedBlocks.Contains(blockIndex))
                        continue;
                    for (var instructionIndex = block.Start; instructionIndex < block.End; ++instructionIndex)
                        loop.Add(instructions[instructionIndex]);
                }

                loop.NestedInstructionCount =
                    loop.InclusiveInstructionCount - loop.ExclusiveInstructionCount;
            }

            using var pooledRegionBlocks = ConduitUtility.GetPooledSet<int>(out var regionBlocks);
            foreach (var loop in loops)
                regionBlocks.UnionWith(loop.Blocks);
            foreach (var blockIndex in regionBlocks)
                stats.LoopRegionInstructionCount += blocks[blockIndex].End - blocks[blockIndex].Start;

            stats.Loops.AddRange(loops);
            stats.LoopBackedgeCount = 0;
            foreach (var loop in loops)
            {
                stats.LoopBackedgeCount += loop.BackedgeCount;
                stats.LoopMaxDepth = Math.Max(stats.LoopMaxDepth, loop.Depth);
            }
            stats.LoopAnalysisCompleted = true;

            void AddEdge(int source, int target)
            {
                if (blocks[source].Successors.Contains(target))
                    return;

                blocks[source].Successors.Add(target);
                blocks[target].Predecessors.Add(source);
            }

            static void AddBit(ulong[] bits, int index)
                => bits[index >> 6] |= 1UL << (index & 63);

            static bool ContainsBit(ulong[] bits, int index)
                => (bits[index >> 6] & 1UL << (index & 63)) != 0;

            static void IntersectBits(ulong[] target, ulong[] other)
            {
                for (var index = 0; index < target.Length; ++index)
                    target[index] &= other[index];
            }

            static bool BitsEqual(ulong[] left, ulong[] right)
            {
                for (var index = 0; index < left.Length; ++index)
                    if (left[index] != right[index])
                        return false;

                return true;
            }

            static int Depth(BurstLoopStats loop) => loop.Parent == null ? 1 : Depth(loop.Parent) + 1;
        }

        static bool TryGetBranchTarget(BurstAnalyzedInstruction instruction, out string target)
        {
            target = instruction.BranchTarget;
            return target.Length > 0;
        }

        static bool IsTrap(string mnemonic) =>
            BaseMnemonic(mnemonic) is "ud2" or "int3" or "brk" or "hlt";

        static string BlockLabel(
            BurstControlFlowBlock block,
            IReadOnlyDictionary<int, List<string>> labelsByInstruction)
        {
            if (labelsByInstruction.TryGetValue(block.Start, out var labels))
            {
                foreach (var label in labels)
                    if (label.StartsWith(".LBB", StringComparison.Ordinal))
                        return label;
                foreach (var label in labels)
                    if (!label.StartsWith(".L", StringComparison.Ordinal))
                        return label;
            }

            return $"B{block.Index}";
        }

        static string FindSource(
            BurstLoopStats loop,
            IReadOnlyList<BurstControlFlowBlock> blocks,
            IReadOnlyList<BurstAnalyzedInstruction> instructions)
        {
            var header = blocks[loop.HeaderBlock];
            for (var index = header.Start; index < header.End; ++index)
                if (instructions[index].Source.Length > 0)
                    return instructions[index].Source;

            var firstSource = string.Empty;
            var firstSourceBlockStart = int.MaxValue;
            foreach (var blockIndex in loop.Blocks)
            {
                var block = blocks[blockIndex];
                if (blockIndex == loop.HeaderBlock || block.Start >= firstSourceBlockStart)
                    continue;

                for (var instructionIndex = block.Start; instructionIndex < block.End; ++instructionIndex)
                    if (instructions[instructionIndex].Source.Length > 0)
                    {
                        firstSource = instructions[instructionIndex].Source;
                        firstSourceBlockStart = block.Start;
                        break;
                    }
            }

            return firstSource;
        }

        static void AppendLoopSummary(StringBuilder builder, BurstAsmStats stats)
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

        static void AppendLoops(StringBuilder builder, BurstAsmStats stats)
        {
            if (!stats.LoopAnalysisCompleted || stats.Loops.Count == 0)
                return;

            using var pooledRows = ConduitUtility.GetPooledList<(BurstLoopStats Loop, int Indent)>(out var rows);
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

    readonly struct BurstAnalyzedInstruction
    {
        public readonly string Mnemonic;
        public readonly string BranchTarget;
        public readonly string Source;
        public readonly BurstInstructionFacts Facts;
        public readonly bool IsConditionalBranch;
        public readonly bool IsUnconditionalBranch;

        public BurstAnalyzedInstruction(
            string mnemonic,
            string branchTarget,
            string source,
            BurstInstructionFacts facts,
            bool isConditionalBranch,
            bool isUnconditionalBranch)
        {
            Mnemonic = mnemonic;
            BranchTarget = branchTarget;
            Source = source;
            Facts = facts;
            IsConditionalBranch = isConditionalBranch;
            IsUnconditionalBranch = isUnconditionalBranch;
        }
    }

    sealed class BurstControlFlowBlock
    {
        public readonly int Index;
        public readonly int Start;
        public readonly int End;
        public readonly List<int> Successors = new();
        public readonly List<int> Predecessors = new();
        public string IncompleteReason = string.Empty;
        public int ExitPathCount;

        public BurstControlFlowBlock(int index, int start, int end)
        {
            Index = index;
            Start = start;
            End = end;
        }
    }

    sealed class BurstLoopStats
    {
        public readonly int HeaderBlock;
        public readonly HashSet<int> Blocks;
        public readonly int BackedgeCount;
        public readonly List<BurstLoopStats> Children = new();
        public BurstLoopStats? Parent;
        public string Header = string.Empty;
        public string Source = string.Empty;
        public int Id;
        public int Depth;
        public int InclusiveInstructionCount;
        public int ExclusiveInstructionCount;
        public int NestedInstructionCount;
        public int ExitCount;
        public int LoadCount;
        public int StoreCount;
        public int PackedComputeCount;
        public int BranchCount;
        public int DirectCallCount;
        public int IndirectCallCount;

        public BurstLoopStats(int headerBlock, HashSet<int> blocks, int backedgeCount)
        {
            HeaderBlock = headerBlock;
            Blocks = blocks;
            BackedgeCount = backedgeCount;
        }

        public void Add(BurstAnalyzedInstruction instruction)
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
