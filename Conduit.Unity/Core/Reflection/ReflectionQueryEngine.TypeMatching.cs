#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        internal static TypeMatch MatchSingleType(
            IReadOnlyList<Type> index,
            string query,
            ReflectTypeKind kind = ReflectTypeKind.Any)
        {
            lock (IndexLock)
                if (ReferenceEquals(index, cachedIndex)
                    && GetExactTypeLookup().TryGetValue(query, out var indexed)
                    && indexed is { } indexedType
                    && MatchesTypeKind(indexedType, kind))
                    return TypeMatch.Matched(indexedType);

            var hasAssemblyQuery = query.IndexOf(',') >= 0;
            var hasGenericDisplayQuery = query.IndexOf('<') >= 0;
            var hasNestedDisplayQuery = query.IndexOf('.') >= 0;
            var bestRank = int.MaxValue;
            var matchCount = 0;
            var matches = new List<Type>(MaxCandidates);
            var workerCount = GetParallelScanWorkerCount(index.Count);
            if (workerCount == 1)
                Scan(0, index.Count, matches, ref bestRank, ref matchCount);
            else
            {
                var workerMatches = new List<Type>[workerCount];
                var workerRanks = new int[workerCount];
                var workerCounts = new int[workerCount];
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new List<Type>(MaxCandidates);
                    var localRank = int.MaxValue;
                    var localCount = 0;
                    var start = (int)((long)index.Count * workerIndex / workerCount);
                    var end = (int)((long)index.Count * (workerIndex + 1) / workerCount);
                    Scan(start, end, localMatches, ref localRank, ref localCount);
                    workerMatches[workerIndex] = localMatches;
                    workerRanks[workerIndex] = localRank;
                    workerCounts[workerIndex] = localCount;
                });

                for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
                {
                    var localRank = workerRanks[workerIndex];
                    if (localRank > bestRank)
                        continue;

                    if (localRank < bestRank)
                    {
                        bestRank = localRank;
                        matchCount = 0;
                        matches.Clear();
                    }

                    matchCount += workerCounts[workerIndex];
                    var localMatches = workerMatches[workerIndex];
                    var toCopy = Math.Min(MaxCandidates - matches.Count, localMatches.Count);
                    for (var matchIndex = 0; matchIndex < toCopy; matchIndex++)
                        matches.Add(localMatches[matchIndex]);
                }
            }

            if (matchCount == 0)
                return TypeMatch.None();

            return SelectTypeMatch(matches, matchCount);

            void Scan(
                int start,
                int end,
                List<Type> destination,
                ref int destinationRank,
                ref int destinationCount)
            {
                for (var position = start; position < end; position++)
                {
                    var type = index[position];
                    var info = GetTypeSearchInfo(index, position);
                    if (kind != ReflectTypeKind.Any && info.Kind != kind)
                        continue;

                    var rank = MatchRank(type, info);
                    if (rank == int.MaxValue || rank > destinationRank)
                        continue;

                    if (rank < destinationRank)
                    {
                        destination.Clear();
                        destinationRank = rank;
                        destinationCount = 0;
                    }

                    destinationCount++;
                    if (destination.Count < MaxCandidates)
                        destination.Add(type);
                }
            }

            int MatchRank(Type type, TypeSearchInfo info)
            {
                var needsDisplayName = hasGenericDisplayQuery && info.IsGenericType
                                       || hasNestedDisplayQuery && info.IsNested;
                var shortDisplayName = needsDisplayName
                    ? info.ShortDisplayName ??= ReflectionTypeFormatter.DisplayTypeName(
                        type,
                        includeNamespace: false
                    )
                    : null;
                var fullDisplayName = needsDisplayName
                    ? ReflectionTypeFormatter.DisplayTypeName(type, includeNamespace: true)
                    : null;
                var qualifiedName = hasAssemblyQuery
                    ? $"{info.FullName}, {info.AssemblyName}"
                    : null;

                if (qualifiedName != null
                    && string.Equals(qualifiedName, query, StringComparison.OrdinalIgnoreCase))
                    return 0;
                if (string.Equals(info.FullName, query, StringComparison.OrdinalIgnoreCase))
                    return 1;
                if (string.Equals(info.Name, query, StringComparison.OrdinalIgnoreCase))
                    return 2;
                if (shortDisplayName != null
                    && (string.Equals(fullDisplayName, query, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(shortDisplayName, query, StringComparison.OrdinalIgnoreCase)))
                    return 3;
                if (Contains(info.FullName, query)
                    || shortDisplayName != null
                    && (Contains(shortDisplayName, query) || Contains(fullDisplayName!, query))
                    || Contains(info.AssemblyName, query)
                    || qualifiedName != null && Contains(qualifiedName, query))
                    return 4;
                return int.MaxValue;
            }
        }

        static TypeMatch SelectTypeMatch(List<Type> matches, int matchCount)
            => matchCount == 1
                ? TypeMatch.Matched(matches[0])
                : TypeMatch.Ambiguous(matches, matchCount);

        internal static bool MatchesTypeKind(Type type, ReflectTypeKind kind)
            => kind == ReflectTypeKind.Any || GetTypeSearchInfo(type).Kind == kind;

        internal static bool MatchesTypeKind(
            IReadOnlyList<Type> index,
            int position,
            ReflectTypeKind kind
        ) => kind == ReflectTypeKind.Any || GetTypeSearchInfo(index, position).Kind == kind;

        internal static bool TryParseMode(string mode, out ReflectMode queryMode)
        {
            queryMode = default;
            switch (NormalizeQuery(mode).ToLowerInvariant())
            {
                case "types":
                    queryMode = new(ReflectCategory.Types, ReflectTypeKind.Any, ReflectMemberKind.None);
                    return true;
                case "classes":
                    queryMode = new(ReflectCategory.Types, ReflectTypeKind.Class, ReflectMemberKind.None);
                    return true;
                case "structs":
                    queryMode = new(ReflectCategory.Types, ReflectTypeKind.Struct, ReflectMemberKind.None);
                    return true;
                case "enums":
                    queryMode = new(ReflectCategory.Types, ReflectTypeKind.Enum, ReflectMemberKind.None);
                    return true;
                case "interfaces":
                    queryMode = new(ReflectCategory.Types, ReflectTypeKind.Interface, ReflectMemberKind.None);
                    return true;
                case "delegates":
                    queryMode = new(ReflectCategory.Types, ReflectTypeKind.Delegate, ReflectMemberKind.None);
                    return true;
                case "members":
                    queryMode = new(ReflectCategory.Members, ReflectTypeKind.Any, ReflectMemberKind.None);
                    return true;
                case "fields":
                    queryMode = new(ReflectCategory.Members, ReflectTypeKind.Any, ReflectMemberKind.Field);
                    return true;
                case "properties":
                    queryMode = new(ReflectCategory.Members, ReflectTypeKind.Any, ReflectMemberKind.Property);
                    return true;
                case "methods":
                    queryMode = new(ReflectCategory.Members, ReflectTypeKind.Any, ReflectMemberKind.Method);
                    return true;
                case "constructors":
                    queryMode = new(ReflectCategory.Members, ReflectTypeKind.Any, ReflectMemberKind.Constructor);
                    return true;
                default:
                    return false;
            }
        }

        static string InvalidModeDiagnostic(string mode)
            => $"Unsupported reflect mode '{mode}'. Valid modes: {ValidModes}.";

        static string NormalizeQuery(string value)
            => value?.Trim() ?? string.Empty;

        static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
