#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    readonly struct TypeMatch
    {
        internal readonly TypeMatchKind Kind;
        internal readonly Type? Type;
        internal readonly IReadOnlyList<Type> Candidates;
        internal readonly int CandidateCount;
        internal readonly bool IsExact;

        TypeMatch(
            TypeMatchKind kind,
            Type? type,
            IReadOnlyList<Type> candidates,
            int candidateCount,
            bool isExact)
        {
            Kind = kind;
            Type = type;
            Candidates = candidates;
            CandidateCount = candidateCount;
            IsExact = isExact;
        }

        internal static TypeMatch None()
            => new(TypeMatchKind.None, null, Array.Empty<Type>(), 0, false);

        internal static TypeMatch Matched(Type type, bool isExact = true)
            => new(TypeMatchKind.Matched, type, Array.Empty<Type>(), 1, isExact);

        internal static TypeMatch Ambiguous(IReadOnlyList<Type> candidates, int candidateCount, bool isExact)
            => new(TypeMatchKind.Ambiguous, null, candidates, candidateCount, isExact);
    }
}
