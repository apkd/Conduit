#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        internal static FieldInfo[] GetFields(Type type)
            => fieldCache.GetOrAdd(type, static value =>
                value.GetFields(DeclaredMembers)
            );

        internal static PropertyInfo[] GetProperties(Type type)
            => propertyCache.GetOrAdd(type, static value =>
                value.GetProperties(DeclaredMembers)
            );

        internal static MethodInfo[] GetMethods(Type type, string memberQuery = "")
        {
            var methods = methodCache.GetOrAdd(type, static value =>
                value.GetMethods(DeclaredMembers)
            );
            if (IsAccessorQuery(memberQuery))
                return methods;

            if (methodWithoutAccessorsCache.TryGetValue(type, out var filtered))
                return filtered;

            filtered = Array.FindAll(
                methods,
                static method => !ReflectionMemberFormatter.IsPropertyOrEventAccessor(method)
            );
            return methodWithoutAccessorsCache.GetOrAdd(type, filtered);
        }

        internal static MethodInfo[] GetMethods(Type type, bool includeAccessors)
            => includeAccessors ? methodCache.GetOrAdd(type, static value =>
                value.GetMethods(DeclaredMembers)
            ) : GetMethods(type);

        internal static bool IsAccessorQuery(string memberQuery)
            => memberQuery.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("set_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("add_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("remove_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("raise_", StringComparison.OrdinalIgnoreCase);

        internal static ConstructorInfo[] GetConstructors(Type type)
            => constructorCache.GetOrAdd(type, static value =>
                value.GetConstructors(DeclaredMembers)
            );

        internal static bool TypeDeclaresMatchingMember(Type type, ReflectMemberKind kind, string memberQuery)
        {
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                foreach (var field in GetFields(type))
                    if (MatchesMember(field, memberQuery))
                        return true;

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                foreach (var property in GetProperties(type))
                    if (MatchesMember(property, memberQuery))
                        return true;

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
                foreach (var method in GetMethods(type, memberQuery))
                    if (MatchesMember(method, memberQuery))
                        return true;

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                foreach (var constructor in GetConstructors(type))
                    if (MatchesMember(constructor, memberQuery))
                        return true;

            return false;
        }

        internal static HashSet<Type> FindTypesDeclaringMatchingMember(
            IReadOnlyList<Type> types,
            string memberQuery)
        {
            var matches = new HashSet<Type>();
            Append(ReflectMemberKind.Field);
            Append(ReflectMemberKind.Property);
            Append(ReflectMemberKind.Method);
            if (IsAccessorQuery(memberQuery))
                Append(ReflectMemberKind.Method, accessorsOnly: true);
            Append(ReflectMemberKind.Constructor);
            return matches;

            void Append(ReflectMemberKind kind, bool accessorsOnly = false)
            {
                var segments = GetWideMemberIndex(types, kind, accessorsOnly).Segments;
                var entryCount = 0;
                foreach (var segment in segments)
                    entryCount += segment.Entries.Length;

                // metadata and search strings are immutable; partition large scans to reduce the editor stall.
                var workerCount = GetParallelScanWorkerCount(entryCount);
                if (workerCount == 1)
                {
                    foreach (var segment in segments)
                        AppendSegment(matches, segment);
                    return;
                }

                var workerResults = new HashSet<Type>[workerCount];
                var nextSegment = -1;
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
                        if (TryGetMemberMatchRank(
                                entry.Name,
                                entry.DeclaringType,
                                kind == ReflectMemberKind.Constructor,
                                memberQuery,
                                out _
                            ))
                            destination.Add(entry.DeclaringType);
                }
            }
        }

        static bool MatchesMember(MemberInfo member, string query)
            => MemberMatchRank(member, query) < int.MaxValue;

        static bool TryGetMemberMatchRank(MemberInfo member, string query, out int rank)
        {
            rank = MemberMatchRank(member, query);
            return rank < int.MaxValue;
        }

        static bool TryGetMemberMatchRank(
            string memberName,
            Type declaringType,
            bool isConstructor,
            string query,
            out int rank)
        {
            rank = MemberMatchRank(memberName, declaringType, isConstructor, query);
            return rank < int.MaxValue;
        }

        internal static int MemberMatchRank(MemberInfo member, string query)
            => MemberMatchRank(
                member.Name,
                member.DeclaringType ?? typeof(object),
                member is ConstructorInfo,
                query
            );

        internal static int MemberMatchRank(
            string memberName,
            Type declaringType,
            bool isConstructor,
            string query)
        {
            if (query.Length == 0)
                return 0;

            var nameRank = TextMatchRank(memberName, query);
            if (nameRank < int.MaxValue)
                return nameRank;

            if (isConstructor)
            {
                if (string.Equals(query, "ctor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(query, ".ctor", StringComparison.OrdinalIgnoreCase))
                    return 0;

                var shortName = ShortTypeName(declaringType);
                return TextMatchRank(shortName, query);
            }

            return int.MaxValue;
        }

        static int TextMatchRank(string value, string query)
        {
            var offset = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (offset < 0)
                return int.MaxValue;
            if (offset > 0)
                return 2;
            return value.Length == query.Length ? 0 : 1;
        }

    }
}
