#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    enum BurstAsmTargetMatchKind : byte
    {
        None,
        Matched,
        Ambiguous,
    }

    readonly struct BurstAsmTargetMatch
    {
        internal readonly BurstAsmTargetMatchKind Kind;
        internal readonly int SelectedIndex;
        internal readonly int[] CandidateIndexes;
        internal readonly int CandidateCount;

        BurstAsmTargetMatch(
            BurstAsmTargetMatchKind kind,
            int selectedIndex,
            int[] candidateIndexes,
            int candidateCount)
        {
            Kind = kind;
            SelectedIndex = selectedIndex;
            CandidateIndexes = candidateIndexes;
            CandidateCount = candidateCount;
        }

        internal static BurstAsmTargetMatch Matched(int index) =>
            new(BurstAsmTargetMatchKind.Matched, index, Array.Empty<int>(), 0);

        internal static BurstAsmTargetMatch Ambiguous(IReadOnlyList<int> indexes) =>
            new(BurstAsmTargetMatchKind.Ambiguous, -1, Copy(indexes), indexes.Count);

        internal static BurstAsmTargetMatch None(IReadOnlyList<int> indexes, int? candidateCount = null) =>
            new(BurstAsmTargetMatchKind.None, -1, Copy(indexes), candidateCount ?? indexes.Count);

        static int[] Copy(IReadOnlyList<int> indexes)
        {
            var copy = new int[Math.Min(indexes.Count, MaxCandidates)];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = indexes[i];

            return copy;
        }

        const int MaxCandidates = 10;
    }
}
