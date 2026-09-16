#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        static readonly ConcurrentDictionary<Type, MemberInfo[]> searchMemberCache = new();
        static readonly ConcurrentDictionary<Type, MemberInfo[]> searchMemberWithoutAccessorsCache = new();

        static MemberInfo[] GetSearchMembers(Type type, ReflectMemberKind kind, MemberQuery memberQuery)
        {
            bool includeAccessors = memberQuery.IncludesAccessors;
            return kind switch
            {
                ReflectMemberKind.Field => GetFields(type),
                ReflectMemberKind.Property => GetProperties(type),
                ReflectMemberKind.Method => GetMethods(type, includeAccessors),
                ReflectMemberKind.Constructor => GetConstructors(type),
                _ => includeAccessors
                    ? searchMemberCache.GetOrAdd(type, static value => CreateMembers(value, GetMethods(value, includeAccessors: true)))
                    : searchMemberWithoutAccessorsCache.GetOrAdd(type, static value => CreateMembers(value, GetMethods(value))),
            };

            static MemberInfo[] CreateMembers(Type value, MethodInfo[] methods)
            {
                var fields = GetFields(value);
                var properties = GetProperties(value);
                var constructors = GetConstructors(value);
                var members = new MemberInfo[fields.Length + properties.Length + methods.Length + constructors.Length];
                fields.CopyTo(members, 0);
                properties.CopyTo(members, fields.Length);
                methods.CopyTo(members, fields.Length + properties.Length);
                constructors.CopyTo(members, fields.Length + properties.Length + methods.Length);
                return members;
            }
        }

        internal static FieldInfo[] GetFields(Type type)
            => fieldCache.GetOrAdd(type, static value =>
                value.GetFields(DeclaredMembers)
            );

        internal static PropertyInfo[] GetProperties(Type type)
            => propertyCache.GetOrAdd(type, static value =>
                value.GetProperties(DeclaredMembers)
            );

        internal static MethodInfo[] GetMethods(Type type, bool includeAccessors = false)
        {
            var methods = methodCache.GetOrAdd(type, static value =>
                value.GetMethods(DeclaredMembers)
            );
            if (includeAccessors)
                return methods;

            if (methodWithoutAccessorsCache.TryGetValue(type, out var filtered))
                return filtered;

            filtered = Array.FindAll(
                methods,
                static method => !ReflectionMemberFormatter.IsPropertyOrEventAccessor(method)
            );
            return methodWithoutAccessorsCache.GetOrAdd(type, filtered);
        }

        internal static ConstructorInfo[] GetConstructors(Type type)
            => constructorCache.GetOrAdd(type, static value =>
                value.GetConstructors(DeclaredMembers)
            );

        internal static bool TypeDeclaresMatchingMember(Type type, ReflectMemberKind kind, MemberQuery memberQuery)
        {
            foreach (var member in GetSearchMembers(type, kind, memberQuery))
                if (MatchesMember(member, memberQuery))
                    return true;

            return false;
        }

        internal static HashSet<Type> FindTypesDeclaringMatchingMember(
            IReadOnlyList<Type> types,
            MemberQuery memberQuery)
        {
            var matches = new HashSet<Type>();
            Append(ReflectMemberKind.Field);
            Append(ReflectMemberKind.Property);
            Append(ReflectMemberKind.Method);
            if (memberQuery.IncludesAccessors)
                Append(ReflectMemberKind.Method, accessorsOnly: true);
            Append(ReflectMemberKind.Constructor);
            return matches;

            void Append(ReflectMemberKind kind, bool accessorsOnly = false)
            {
                var segments = GetWideMemberIndex(types, kind, accessorsOnly).Segments;
                int entryCount = 0;
                foreach (var segment in segments)
                    entryCount += segment.Entries.Length;

                // metadata and search strings are immutable; partition large scans to reduce the editor stall.
                int workerCount = GetParallelScanWorkerCount(entryCount);
                if (workerCount == 1)
                {
                    foreach (var segment in segments)
                        AppendSegment(matches, segment);
                    return;
                }

                var workerResults = new HashSet<Type>[workerCount];
                int nextSegment = -1;
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new HashSet<Type>();
                    int segmentIndex;
                    while ((segmentIndex = Interlocked.Increment(ref nextSegment)) < segments.Length)
                        AppendSegment(localMatches, segments[segmentIndex]);

                    workerResults[workerIndex] = localMatches;
                });

                foreach (var workerResult in workerResults)
                    matches.UnionWith(workerResult);

                void AppendSegment(HashSet<Type> destination, WideMemberIndexSegment segment)
                {
                    foreach (var entry in segment.Entries)
                        if (TryGetMemberMatchRank(entry, memberQuery, out _))
                            destination.Add(entry.DeclaringType);
                }
            }
        }

        static bool MatchesMember(MemberInfo member, MemberQuery query)
            => MemberMatchRank(member, query) < int.MaxValue;

        static bool TryGetMemberMatchRank(MemberInfo member, MemberQuery query, out int rank)
        {
            rank = MemberMatchRank(member, query);
            return rank < int.MaxValue;
        }

        static bool TryGetMemberMatchRank(
            WideMemberIndexEntry member,
            MemberQuery query,
            out int rank)
        {
            rank = query.HasSignature
                ? query.SignatureMatchRank(member.Member)
                : MemberNameMatchRank(member.Name, member.DeclaringType, member.Member is ConstructorInfo, query.Text);
            return rank < int.MaxValue;
        }

        internal static int MemberMatchRank(MemberInfo member, MemberQuery query)
            => query.HasSignature
                ? query.SignatureMatchRank(member)
                : MemberNameMatchRank(
                    member.Name,
                    member.DeclaringType ?? typeof(object),
                    member is ConstructorInfo,
                    query.Text
                );

        static int MemberNameMatchRank(
            string memberName,
            Type declaringType,
            bool isConstructor,
            string query)
        {
            if (query.Length == 0)
                return 0;

            if (isConstructor)
            {
                if (string.Equals(query, "ctor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(query, ".ctor", StringComparison.OrdinalIgnoreCase))
                    return memberName == ".ctor" ? NameMatching.Exact : NameMatching.None;
                if (string.Equals(query, "cctor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(query, ".cctor", StringComparison.OrdinalIgnoreCase))
                    return memberName == ".cctor" ? NameMatching.Exact : NameMatching.None;
            }

            var nameRank = NameMatching.Rank(memberName, query);
            if (nameRank < int.MaxValue)
                return nameRank;

            if (isConstructor)
            {
                var shortName = ShortTypeName(declaringType);
                return NameMatching.Rank(shortName, query);
            }

            return int.MaxValue;
        }

    }
}
