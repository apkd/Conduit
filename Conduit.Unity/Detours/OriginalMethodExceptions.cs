#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Conduit
{
    /// <summary>Rebuilds structured exception regions through Mono's supported ILGenerator API.</summary>
    sealed class OriginalMethodExceptions
    {
        readonly Region[] regions;
        readonly HashSet<int> implicitTerminators = new();

        internal OriginalMethodExceptions(MethodBody body, List<OriginalMethodIL.Instruction> instructions)
        {
            regions = body.ExceptionHandlingClauses.Cast<ExceptionHandlingClause>()
                .GroupBy(c => (c.TryOffset, c.TryLength))
                .Select(g => new Region(g.Key.TryOffset, g.Key.TryOffset + g.Key.TryLength,
                    g.OrderBy(c => c.HandlerOffset).ToArray()))
                .OrderBy(r => r.Start).ThenByDescending(r => r.End).ToArray();
            var boundaries = new HashSet<int>(instructions.Select(i => i.Offset)) { body.GetILAsByteArray()!.Length };
            foreach (var region in regions)
            {
                int previousEnd = region.TryEnd;
                foreach (var clause in region.Clauses)
                {
                    int start = clause.Flags == ExceptionHandlingClauseOptions.Filter ? clause.FilterOffset : clause.HandlerOffset;
                    int end = clause.HandlerOffset + clause.HandlerLength;
                    if (start != previousEnd || !boundaries.Contains(start) || !boundaries.Contains(clause.HandlerOffset)
                        || !boundaries.Contains(end) || !boundaries.Contains(region.Start))
                        throw Unsupported(start);
                    if (clause.Flags is ExceptionHandlingClauseOptions.Finally or ExceptionHandlingClauseOptions.Fault)
                    {
                        if (region.Clauses.Length != 1)
                            throw Unsupported(start);
                        SuppressTerminator(end, OpCodes.Endfinally);
                    }
                    else if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                        SuppressTerminator(clause.HandlerOffset, OpCodes.Endfilter);
                    else if (clause.Flags != ExceptionHandlingClauseOptions.Clause)
                        throw Unsupported(start);
                    previousEnd = end;
                }

                foreach (var other in regions)
                {
                    if (other == region || other.Start >= region.End || other.End <= region.Start)
                        continue;
                    if (other.Start <= region.Start && other.End >= region.End)
                    {
                        if (other.Start == region.Start && other.End == region.End)
                            throw Unsupported(region.Start);
                        continue;
                    }
                    var sectionEnds = new[] { region.Start, region.TryEnd }.Concat(region.Clauses.SelectMany(c =>
                        c.Flags == ExceptionHandlingClauseOptions.Filter
                            ? new[] { c.FilterOffset, c.HandlerOffset, c.HandlerOffset + c.HandlerLength }
                            : new[] { c.HandlerOffset, c.HandlerOffset + c.HandlerLength })).Distinct().OrderBy(x => x).ToArray();
                    if (!sectionEnds.Zip(sectionEnds.Skip(1), (start, end) => other.Start >= start && other.End <= end).Any(x => x))
                        throw Unsupported(other.Start);
                }
            }
            foreach (var instruction in instructions)
                if ((instruction.OpCode == OpCodes.Endfilter || instruction.OpCode == OpCodes.Endfinally)
                    && !implicitTerminators.Contains(instruction.Offset))
                    throw Unsupported(instruction.Offset);

            void SuppressTerminator(int end, OpCode expected)
            {
                var last = instructions.Last(i => i.Offset < end);
                if (last.OpCode.FlowControl == FlowControl.Throw)
                    return; // an always-throwing handler needs no reachable implicit terminator
                if (last.OpCode != expected)
                    throw Unsupported(last.Offset);
                implicitTerminators.Add(last.Offset); // ILGenerator emits this at the next handler boundary
            }
        }

        internal bool IsImplicitTerminator(int offset) => implicitTerminators.Contains(offset);

        internal void EmitBoundary(ILGenerator il, int offset)
        {
            foreach (var region in regions.Reverse())
                if (region.End == offset)
                    il.EndExceptionBlock();
            foreach (var region in regions)
                foreach (var clause in region.Clauses)
                {
                    if (clause.Flags == ExceptionHandlingClauseOptions.Filter && clause.FilterOffset == offset)
                        il.BeginExceptFilterBlock();
                    if (clause.HandlerOffset != offset)
                        continue;
                    switch (clause.Flags)
                    {
                        case ExceptionHandlingClauseOptions.Clause: il.BeginCatchBlock(clause.CatchType); break;
                        case ExceptionHandlingClauseOptions.Filter: il.BeginCatchBlock(null); break;
                        case ExceptionHandlingClauseOptions.Finally: il.BeginFinallyBlock(); break;
                        case ExceptionHandlingClauseOptions.Fault: il.BeginFaultBlock(); break;
                    }
                }
            foreach (var region in regions)
                if (region.Start == offset)
                    il.BeginExceptionBlock();
        }

        static NotSupportedException Unsupported(int offset)
            => new($"exception layout cannot be reproduced at IL_{offset:x4}");

        sealed class Region
        {
            internal readonly int Start;
            internal readonly int TryEnd;
            internal readonly ExceptionHandlingClause[] Clauses;
            internal int End => Clauses[Clauses.Length - 1].HandlerOffset + Clauses[Clauses.Length - 1].HandlerLength;

            internal Region(int start, int tryEnd, ExceptionHandlingClause[] clauses)
                => (Start, TryEnd, Clauses) = (start, tryEnd, clauses);
        }
    }
}
