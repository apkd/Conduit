#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    readonly struct MemberMatch
    {
        internal readonly Type DeclaringType;
        internal readonly ReflectMemberKind Kind;
        internal readonly string Name;
        internal readonly MemberInfo Member;
        internal readonly int MatchRank;

        internal MemberMatch(
            Type declaringType,
            ReflectMemberKind kind,
            string name,
            MemberInfo member,
            int matchRank)
        {
            DeclaringType = declaringType;
            Kind = kind;
            Name = name;
            Member = member;
            MatchRank = matchRank;
        }
    }
}
