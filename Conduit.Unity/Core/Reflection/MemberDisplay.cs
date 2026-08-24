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

        internal MemberDisplay(Type declaringType, ReflectMemberKind kind, string name, string signature, int matchRank)
        {
            DeclaringType = declaringType;
            Kind = kind;
            Name = name;
            Signature = signature;
            MatchRank = matchRank;
        }
    }
}
