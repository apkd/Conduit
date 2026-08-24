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

        TypeMatch(
            TypeMatchKind kind,
            Type? type,
            IReadOnlyList<Type> candidates,
            int candidateCount)
        {
            Kind = kind;
            Type = type;
            Candidates = candidates;
            CandidateCount = candidateCount;
        }

        internal static TypeMatch None()
            => new(TypeMatchKind.None, null, Array.Empty<Type>(), 0);

        internal static TypeMatch Matched(Type type)
            => new(TypeMatchKind.Matched, type, Array.Empty<Type>(), 1);

        internal static TypeMatch Ambiguous(IReadOnlyList<Type> candidates, int candidateCount)
            => new(TypeMatchKind.Ambiguous, null, candidates, candidateCount);
    }
}
