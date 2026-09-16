#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        internal static MemberInfo[] FindMembers(IReadOnlyList<Type> index, ReflectMemberKind kind, string? typeQuery, MemberQuery memberQuery)
        {
            var normalizedType = NormalizeQuery(typeQuery);
            if (normalizedType.Length == 0 && memberQuery.Text.Length == 0)
                throw new InvalidOperationException("reflect member modes require `type` or `member`.");

            if (normalizedType.Length == 0)
                return CollectWideMembers(index, memberQuery, kind);

            var matches = new List<MemberInfo>();
            CollectTypeScopedMembers(index, normalizedType, memberQuery, kind, matches);
            matches.Sort(CompareMembers);
            return matches.ToArray();
        }

        static void CollectTypeScopedMembers(
            IReadOnlyList<Type> index,
            string typeQuery,
            MemberQuery memberQuery,
            ReflectMemberKind kind,
            List<MemberInfo> matches
        )
        {
            var match = MatchSingleType(index, typeQuery);
            if (match.Kind == TypeMatchKind.None)
                throw new InvalidOperationException($"No type matched '{typeQuery}'.");

            if (match.Kind == TypeMatchKind.Ambiguous)
                throw new InvalidOperationException(TypeCandidates(
                    $"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.",
                    match.Candidates,
                    match.CandidateCount
                ));

            var target = match.Type!;
            Append(target);

            // type-scoped reflection mirrors the report tool: once a target type is selected,
            // inherited and interface members are usually what the snippet author needs next.
            for (var baseType = target.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
                Append(baseType);

            var interfaces = target.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);
            foreach (var interfaceType in interfaces)
                Append(interfaceType);

            void Append(Type type)
            {
                foreach (var member in GetSearchMembers(type, kind, memberQuery))
                    if (MatchesMember(member, memberQuery))
                        matches.Add(member);
            }
        }

        static MemberInfo[] CollectWideMembers(
            IReadOnlyList<Type> index,
            MemberQuery memberQuery,
            ReflectMemberKind kind)
        {
            // wide searches stay declared-only so the same inherited method is reported once per declaring type.
            bool includeAccessors = memberQuery.IncludesAccessors;
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

            int matchCount = matches.Count;
            if (matchesByKind != null)
                foreach (var values in matchesByKind)
                    matchCount += values.Count;
            if (matchCount == 0)
                return Array.Empty<MemberInfo>();

            var results = new MemberInfo[matchCount];
            if (matchesByKind == null)
                for (int resultIndex = 0; resultIndex < matches.Count; ++resultIndex)
                    results[resultIndex] = matches[resultIndex].Member;
            else
                MergeMatches(results, matchesByKind);
            return results;

            void Append(
                ReflectMemberKind memberKind,
                List<WideMemberIndexEntry> destination,
                bool accessorsOnly = false)
            {
                var members = GetWideMemberIndex(index, memberKind, accessorsOnly);
                var segments = members.Segments;
                int entryCount = 0;
                foreach (var segment in segments)
                    entryCount += segment.Entries.Length;

                int workerCount = GetParallelScanWorkerCount(entryCount);
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
                    int start = (int)((long)entryCount * workerIndex / workerCount);
                    int end = (int)((long)entryCount * (workerIndex + 1) / workerCount);
                    AppendRange(start, end, localMatches);
                    workerMatches[workerIndex] = localMatches;
                });

                int matchCount = destination.Count;
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
                    int segmentStart = 0;
                    foreach (var segment in segments)
                    {
                        int segmentEnd = segmentStart + segment.Entries.Length;
                        if (segmentEnd <= start)
                        {
                            segmentStart = segmentEnd;
                            continue;
                        }
                        if (segmentStart >= end)
                            return;

                        int first = Math.Max(0, start - segmentStart);
                        int last = Math.Min(segment.Entries.Length, end - segmentStart);
                        for (int entryIndex = first; entryIndex < last; entryIndex++)
                        {
                            var member = segment.Entries[entryIndex];
                            if (TryGetMemberMatchRank(member, memberQuery, out _))
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
                int leftIndex = 0;
                int rightIndex = 0;
                while (leftIndex < left.Count && rightIndex < right.Count)
                    merged.Add(CompareWideMemberEntries(
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

            static void MergeMatches(MemberInfo[] destination, List<WideMemberIndexEntry>[] sources)
            {
                // each member kind is already sorted by declaring type; merge those groups without sorting all matches again.
                var positions = new int[sources.Length];
                int destinationIndex = 0;
                while (destinationIndex < destination.Length)
                {
                    Type? nextType = null;
                    for (int sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
                    {
                        if (positions[sourceIndex] == sources[sourceIndex].Count)
                            continue;

                        var declaringType = sources[sourceIndex][positions[sourceIndex]].DeclaringType;
                        if (nextType == null || CompareTypes(declaringType, nextType) < 0)
                            nextType = declaringType;
                    }

                    for (int sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
                    {
                        var source = sources[sourceIndex];
                        while (positions[sourceIndex] < source.Count)
                        {
                            var entry = source[positions[sourceIndex]];
                            if (!ReferenceEquals(entry.DeclaringType, nextType)
                                && CompareTypes(entry.DeclaringType, nextType) != 0)
                                break;

                            destination[destinationIndex++] = entry.Member;
                            positions[sourceIndex]++;
                        }
                    }
                }
            }
        }

        static int CompareMembers(MemberInfo left, MemberInfo right)
        {
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
