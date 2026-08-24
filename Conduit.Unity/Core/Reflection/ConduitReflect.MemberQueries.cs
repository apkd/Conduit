#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Conduit
{
    public static partial class ConduitReflect
    {
        static T[] FindMembers<T>(IReadOnlyList<Type> index, ReflectMode mode, string? typeQuery, string? memberQuery) where T : class
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = NormalizeQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Length == 0)
                throw new InvalidOperationException("reflect member modes require `type` or `member`.");

            var effectiveKind = GetEffectiveMemberKind<T>(mode.MemberKind);
            if (normalizedType.Length == 0)
                return CollectWideMembers<T>(index, normalizedMember, effectiveKind);

            var matches = new List<MemberInfo>();
            CollectTypeScopedMembers(index, normalizedType, normalizedMember, effectiveKind, matches);
            SortMembers(matches);
            return CastResults<T, MemberInfo>(matches);
        }

        static void CollectTypeScopedMembers(
            IReadOnlyList<Type> index,
            string typeQuery,
            string memberQuery,
            ReflectMemberKind kind,
            List<MemberInfo> matches
        )
        {
            var match = ReflectionQueryEngine.MatchSingleType(index, typeQuery);
            if (match.Kind == TypeMatchKind.None)
                throw new InvalidOperationException($"No type matched '{typeQuery}'.");

            if (match.Kind == TypeMatchKind.Ambiguous)
                throw new InvalidOperationException(TypeCandidates(
                    $"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.",
                    match.Candidates,
                    match.CandidateCount
                ));

            var target = match.Type!;
            CollectDeclaredMembers(target, kind, memberQuery, matches);

            // type-scoped reflection mirrors the report tool: once a target type is selected,
            // inherited and interface members are usually what the snippet author needs next.
            for (var baseType = target.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
                CollectDeclaredMembers(baseType, kind, memberQuery, matches);

            var interfaces = target.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);
            foreach (var interfaceType in interfaces)
                CollectDeclaredMembers(interfaceType, kind, memberQuery, matches);
        }

        static T[] CollectWideMembers<T>(
            IReadOnlyList<Type> index,
            string memberQuery,
            ReflectMemberKind kind)
            where T : class
        {
            // wide searches stay declared-only so the same inherited method is reported once per declaring type.
            var includeAccessors = ReflectionQueryEngine.IsAccessorQuery(memberQuery);
            var matches = new List<WideMemberIndexEntry>();
            var matchesByKind = kind == ReflectMemberKind.None
                ? new[]
                {
                    new List<WideMemberIndexEntry>(),
                    new List<WideMemberIndexEntry>(),
                    new List<WideMemberIndexEntry>(),
                    new List<WideMemberIndexEntry>(),
                }
                : null;
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                Append(ReflectMemberKind.Field, matchesByKind?[0] ?? matches);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                Append(ReflectMemberKind.Property, matchesByKind?[1] ?? matches);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
            {
                var methodMatches = matchesByKind?[2] ?? matches;
                Append(ReflectMemberKind.Method, methodMatches);
                if (includeAccessors)
                {
                    var accessorMatches = new List<WideMemberIndexEntry>();
                    Append(
                        ReflectMemberKind.Method,
                        accessorMatches,
                        accessorsOnly: true
                    );
                    methodMatches = MergeSortedMatches(methodMatches, accessorMatches);
                    if (matchesByKind == null)
                        matches = methodMatches;
                    else
                        matchesByKind[2] = methodMatches;
                }
            }
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                Append(ReflectMemberKind.Constructor, matchesByKind?[3] ?? matches);

            var matchCount = matches.Count;
            if (matchesByKind != null)
                foreach (var values in matchesByKind)
                    matchCount += values.Count;
            if (matchCount == 0)
                return Array.Empty<T>();

            var results = new T[matchCount];
            if (matchesByKind == null)
                for (var resultIndex = 0; resultIndex < matches.Count; ++resultIndex)
                    results[resultIndex] = (T)(object)matches[resultIndex].Member;
            else
                MergeMatches(results, matchesByKind);
            return results;

            void Append(
                ReflectMemberKind memberKind,
                List<WideMemberIndexEntry> destination,
                bool accessorsOnly = false)
            {
                var members = ReflectionQueryEngine.GetWideMemberIndex(index, memberKind, accessorsOnly);
                var segments = members.Segments;
                var entryCount = 0;
                foreach (var segment in segments)
                    entryCount += segment.Entries.Length;

                var workerCount = ReflectionQueryEngine.GetParallelScanWorkerCount(entryCount);
                if (workerCount == 1)
                {
                    AppendRange(0, entryCount, destination);
                    return;
                }

                // logical entry ranges balance large assemblies while preserving index order on merge.
                var workerMatches = new List<WideMemberIndexEntry>[workerCount];
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new List<WideMemberIndexEntry>();
                    var start = (int)((long)entryCount * workerIndex / workerCount);
                    var end = (int)((long)entryCount * (workerIndex + 1) / workerCount);
                    AppendRange(start, end, localMatches);
                    workerMatches[workerIndex] = localMatches;
                });

                var matchCount = destination.Count;
                foreach (var workerResult in workerMatches)
                    matchCount += workerResult.Count;
                if (destination.Capacity < matchCount)
                    destination.Capacity = matchCount;
                foreach (var workerResult in workerMatches)
                    destination.AddRange(workerResult);

                void AppendRange(
                    int start,
                    int end,
                    List<WideMemberIndexEntry> rangeMatches)
                {
                    var segmentStart = 0;
                    foreach (var segment in segments)
                    {
                        var segmentEnd = segmentStart + segment.Entries.Length;
                        if (segmentEnd <= start)
                        {
                            segmentStart = segmentEnd;
                            continue;
                        }
                        if (segmentStart >= end)
                            return;

                        var first = Math.Max(0, start - segmentStart);
                        var last = Math.Min(segment.Entries.Length, end - segmentStart);
                        for (var entryIndex = first; entryIndex < last; entryIndex++)
                        {
                            var member = segment.Entries[entryIndex];
                            if (MatchesMember(member, memberQuery))
                                rangeMatches.Add(member);
                        }

                        segmentStart = segmentEnd;
                    }
                }
            }

            static List<WideMemberIndexEntry> MergeSortedMatches(
                List<WideMemberIndexEntry> left,
                List<WideMemberIndexEntry> right)
            {
                if (left.Count == 0)
                    return right;
                if (right.Count == 0)
                    return left;

                var merged = new List<WideMemberIndexEntry>(left.Count + right.Count);
                var leftIndex = 0;
                var rightIndex = 0;
                while (leftIndex < left.Count && rightIndex < right.Count)
                    merged.Add(ReflectionQueryEngine.CompareWideMemberEntries(
                        left[leftIndex],
                        right[rightIndex]
                    ) <= 0
                        ? left[leftIndex++]
                        : right[rightIndex++]);

                while (leftIndex < left.Count)
                    merged.Add(left[leftIndex++]);
                while (rightIndex < right.Count)
                    merged.Add(right[rightIndex++]);
                return merged;
            }

            static void MergeMatches(T[] destination, List<WideMemberIndexEntry>[] sources)
            {
                var positions = new int[sources.Length];
                var destinationIndex = 0;
                while (destinationIndex < destination.Length)
                {
                    Type? nextType = null;
                    for (var sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
                    {
                        if (positions[sourceIndex] == sources[sourceIndex].Count)
                            continue;

                        var declaringType = sources[sourceIndex][positions[sourceIndex]].DeclaringType;
                        if (nextType == null || ReflectionQueryEngine.CompareTypes(declaringType, nextType) < 0)
                            nextType = declaringType;
                    }

                    for (var sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
                    {
                        var source = sources[sourceIndex];
                        while (positions[sourceIndex] < source.Count)
                        {
                            var entry = source[positions[sourceIndex]];
                            if (!ReferenceEquals(entry.DeclaringType, nextType)
                                && ReflectionQueryEngine.CompareTypes(entry.DeclaringType, nextType) != 0)
                                break;

                            destination[destinationIndex++] = (T)(object)entry.Member;
                            positions[sourceIndex]++;
                        }
                    }
                }
            }
        }

        static void CollectDeclaredMembers(Type type, ReflectMemberKind kind, string memberQuery, List<MemberInfo> matches)
        {
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                foreach (var field in GetFields(type))
                    if (MatchesMember(field, memberQuery))
                        matches.Add(field);

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                foreach (var property in GetProperties(type))
                    if (MatchesMember(property, memberQuery))
                        matches.Add(property);

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
                foreach (var method in GetMethods(type, memberQuery))
                    if (MatchesMember(method, memberQuery))
                        matches.Add(method);

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                foreach (var constructor in GetConstructors(type))
                    if (MatchesMember(constructor, memberQuery))
                        matches.Add(constructor);
        }

    }
}

