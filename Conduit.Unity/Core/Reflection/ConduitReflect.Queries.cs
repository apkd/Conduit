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
        const int MaxCandidates = 25;
        const string ValidModes = "types, classes, structs, enums, interfaces, delegates, members, fields, properties, methods, constructors";

        static T[] FindManyCore<T>(string mode, string? type, string? member) where T : class
        {
            if (!ReflectionQueryEngine.TryParseMode(mode, out var queryMode))
                throw new InvalidOperationException(InvalidModeDiagnostic(mode));

            ValidateResultType<T>(queryMode);
            var index = ReflectionQueryEngine.LoadIndexForHelpers();
            return queryMode.Category == ReflectCategory.Types
                ? FindTypes<T>(index, queryMode, type, member)
                : FindMembers<T>(index, queryMode, type, new MemberQuery(member));
        }

        static T FindOneCore<T>(string mode, string? type, string? member) where T : class
        {
            if (!ReflectionQueryEngine.TryParseMode(mode, out var queryMode))
                throw new InvalidOperationException(InvalidModeDiagnostic(mode));

            ValidateResultType<T>(queryMode);
            var index = ReflectionQueryEngine.LoadIndexForHelpers();
            var normalizedType = NormalizeQuery(type);
            if (queryMode.Category == ReflectCategory.Types && normalizedType.Length > 0)
                return FindSingleType<T>(index, queryMode, normalizedType, member);

            var normalizedMember = new MemberQuery(member);
            var matches = queryMode.Category == ReflectCategory.Types
                ? FindTypes<T>(index, queryMode, type, member)
                : FindMembers<T>(index, queryMode, type, normalizedMember);
            if (queryMode.Category == ReflectCategory.Types || matches.Length < 2)
                return SelectSingle(mode, type, member, matches);

            var best = new List<T>();
            int bestRank = NameMatching.None;
            foreach (var value in matches)
                NameMatching.AddBest(best, value,
                    ReflectionQueryEngine.MemberMatchRank((MemberInfo)(object)value, normalizedMember), ref bestRank);
            return SelectSingle(mode, type, member, best);
        }

        static T[] FindMembers<T>(IReadOnlyList<Type> index, ReflectMode mode, string? type, MemberQuery member) where T : class
            => CastResults<T, MemberInfo>(ReflectionQueryEngine.FindMembers(
                index, GetEffectiveMemberKind<T>(mode.MemberKind), type, member));

        static T FindSingleType<T>(IReadOnlyList<Type> index, ReflectMode mode, string typeQuery, string? memberQuery) where T : class
        {
            var normalizedMember = new MemberQuery(memberQuery);
            // singular type lookup keeps the report tool's exact-name precedence before substring matches.
            var match = ReflectionQueryEngine.MatchSingleType(index, typeQuery, mode.TypeKind);

            if (match.Kind == TypeMatchKind.Ambiguous)
                throw new InvalidOperationException(TypeCandidates(
                    $"Multiple reflected results match {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.",
                    match.Candidates,
                    match.CandidateCount
                ));

            if (match.Kind == TypeMatchKind.None)
                throw new InvalidOperationException($"No reflected result matched {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.");

            var type = match.Type!;
            if (normalizedMember.Text.Length > 0
                && !ReflectionQueryEngine.TypeDeclaresMatchingMember(type, ReflectMemberKind.None, normalizedMember))
                throw new InvalidOperationException($"No reflected result matched {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.");

            return (T)(object)type;
        }

        static T[] FindTypes<T>(IReadOnlyList<Type> index, ReflectMode mode, string? typeQuery, string? memberQuery) where T : class
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = new MemberQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Text.Length == 0)
                throw new InvalidOperationException("reflect type modes require `type` or `member`.");

            var typeNameQuery = new TypeNameQuery(normalizedType);
            var declaringTypes = normalizedType.Length == 0 && normalizedMember.Text.Length > 0
                ? ReflectionQueryEngine.FindTypesDeclaringMatchingMember(index, normalizedMember)
                : null;
            var matches = new List<Type>();
            var workerCount = ReflectionQueryEngine.GetParallelScanWorkerCount(index.Count);
            if (workerCount == 1)
                AppendRange(0, index.Count, matches);
            else
            {
                // contiguous worker ranges retain the sorted type order required by the helper API.
                var workerMatches = new List<Type>[workerCount];
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new List<Type>();
                    var start = (int)((long)index.Count * workerIndex / workerCount);
                    var end = (int)((long)index.Count * (workerIndex + 1) / workerCount);
                    AppendRange(start, end, localMatches);
                    workerMatches[workerIndex] = localMatches;
                });

                var matchCount = 0;
                foreach (var workerResult in workerMatches)
                    matchCount += workerResult.Count;
                matches.Capacity = matchCount;
                foreach (var workerResult in workerMatches)
                    matches.AddRange(workerResult);
            }

            // filtering the sorted type index preserves its deterministic output order.
            return CastResults<T, Type>(matches);

            void AppendRange(int start, int end, List<Type> destination)
            {
                for (var position = start; position < end; position++)
                {
                    var type = index[position];
                    if (!ReflectionQueryEngine.MatchesTypeKind(index, position, mode.TypeKind))
                        continue;

                    if (normalizedType.Length > 0
                        && !ReflectionQueryEngine.MatchesTypeName(index, position, typeNameQuery))
                        continue;

                    if (normalizedMember.Text.Length > 0
                        && !(declaringTypes?.Contains(type)
                             ?? ReflectionQueryEngine.TypeDeclaresMatchingMember(type, ReflectMemberKind.None, normalizedMember)))
                        continue;

                    destination.Add(type);
                }
            }
        }

    }
}
