#nullable enable

using System;

namespace Conduit
{
    readonly struct MemberDisplay
    {
        internal readonly Type DeclaringType;
        internal readonly ReflectMemberKind Kind;
        internal readonly string Name;
        internal readonly string Signature;
        internal readonly int MatchRank;
        internal readonly bool IsDetourIncompatible;
        internal readonly bool IsExtern;

        internal MemberDisplay(
            Type declaringType,
            ReflectMemberKind kind,
            string name,
            string signature,
            int matchRank,
            bool isDetourIncompatible,
            bool isExtern)
        {
            DeclaringType = declaringType;
            Kind = kind;
            Name = name;
            Signature = signature;
            MatchRank = matchRank;
            IsDetourIncompatible = isDetourIncompatible;
            IsExtern = isExtern;
        }
    }
}
