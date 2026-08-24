#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace Conduit
{
    public static partial class ConduitReflect
    {
        static bool TryParseMode(string mode, out ReflectMode queryMode) =>
            ReflectionQueryEngine.TryParseMode(mode, out queryMode);

        static bool TypeDeclaresMatchingMember(Type type, ReflectMemberKind kind, string memberQuery)
            => ReflectionQueryEngine.TypeDeclaresMatchingMember(type, kind, memberQuery);

        static FieldInfo[] GetFields(Type type)
            => ReflectionQueryEngine.GetFields(type);

        static PropertyInfo[] GetProperties(Type type)
            => ReflectionQueryEngine.GetProperties(type);

        static MethodInfo[] GetMethods(Type type, string memberQuery) => ReflectionQueryEngine.GetMethods(type, memberQuery);

        static ConstructorInfo[] GetConstructors(Type type)
            => ReflectionQueryEngine.GetConstructors(type);

        static bool MatchesMember(MemberInfo member, string query)
            => MemberMatchRank(member, query) < int.MaxValue;

        static bool MatchesMember(WideMemberIndexEntry member, string query)
            => MemberMatchRank(
                member.Name,
                member.DeclaringType,
                member.Member is ConstructorInfo,
                query
            ) < int.MaxValue;

        static int MemberMatchRank(MemberInfo member, string query)
            => ReflectionQueryEngine.MemberMatchRank(member, query);

        static int MemberMatchRank(
            string memberName,
            Type declaringType,
            bool isConstructor,
            string query)
            => ReflectionQueryEngine.MemberMatchRank(memberName, declaringType, isConstructor, query);

        static void SortMembers(List<MemberInfo> members) => members.Sort(CompareMembers);

        static int CompareMembers(MemberInfo left, MemberInfo right)
        {
            var rank = MemberMatchRank(left, string.Empty).CompareTo(MemberMatchRank(right, string.Empty));
            if (rank != 0)
                return rank;

            var type = CompareTypes(left.DeclaringType, right.DeclaringType);
            if (type != 0)
                return type;

            var kind = GetMemberKind(left).CompareTo(GetMemberKind(right));
            if (kind != 0)
                return kind;

            var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return name != 0
                ? name
                : string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }

        static int CompareTypes(Type? left, Type? right)
            => ReflectionQueryEngine.CompareTypes(left, right);

        static ReflectMemberKind GetMemberKind(MemberInfo member)
            => member switch
            {
                FieldInfo       => ReflectMemberKind.Field,
                PropertyInfo    => ReflectMemberKind.Property,
                MethodInfo      => ReflectMemberKind.Method,
                ConstructorInfo => ReflectMemberKind.Constructor,
                _               => ReflectMemberKind.None,
            };

    }
}
