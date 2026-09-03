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
        const int MaxTypeRows = 100;
        const int MaxWideMemberRows = 200;
        const int MaxCandidates = 25;
        const string ValidModes = "types, classes, structs, enums, interfaces, delegates, members, fields, properties, methods, constructors";

        internal static BridgeCommandResult Reflect(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : string.Empty;
            var type = args.Length > 1 ? args[1] : string.Empty;
            var member = args.Length > 2 ? args[2] : string.Empty;

            if (!TryParseMode(mode, out var queryMode))
                return BridgeCommandResult.Error(InvalidModeDiagnostic(mode));

            var index = LoadIndex(out var loadWarning);
            return queryMode.Category == ReflectCategory.Types
                ? SearchTypes(index, queryMode, type, member, loadWarning)
                : SearchMembers(index, queryMode, type, member, loadWarning);
        }

        static BridgeCommandResult SearchTypes(
            IReadOnlyList<Type> index,
            ReflectMode mode,
            string typeQuery,
            string memberQuery,
            string loadWarning
        )
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = NormalizeQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Length == 0)
                return BridgeCommandResult.Error(
                    "reflect type modes require `type` or `member`. Examples: "
                    + "reflect(\"types\", type: \"Camera\") or reflect(\"types\", member: \"Awake\")."
                );

            var typeNameQuery = new TypeNameQuery(normalizedType);
            var declaringTypes = normalizedType.Length == 0 && normalizedMember.Length > 0
                ? FindTypesDeclaringMatchingMember(index, normalizedMember)
                : null;
            var matches = new List<Type>(MaxTypeRows);
            var totalCount = 0;
            var workerCount = GetParallelScanWorkerCount(index.Count);
            if (workerCount == 1)
                Scan(0, index.Count, matches, ref totalCount);
            else
            {
                // each worker owns a contiguous range so merging preserves the sorted index order.
                var workerMatches = new List<Type>[workerCount];
                var workerCounts = new int[workerCount];
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new List<Type>(MaxTypeRows);
                    var localCount = 0;
                    var start = (int)((long)index.Count * workerIndex / workerCount);
                    var end = (int)((long)index.Count * (workerIndex + 1) / workerCount);
                    Scan(start, end, localMatches, ref localCount);
                    workerMatches[workerIndex] = localMatches;
                    workerCounts[workerIndex] = localCount;
                });

                for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
                {
                    totalCount += workerCounts[workerIndex];
                    var remaining = MaxTypeRows - matches.Count;
                    var localMatches = workerMatches[workerIndex];
                    var toCopy = Math.Min(remaining, localMatches.Count);
                    for (var matchIndex = 0; matchIndex < toCopy; matchIndex++)
                        matches.Add(localMatches[matchIndex]);
                }
            }

            void Scan(int start, int end, List<Type> destination, ref int count)
            {
                for (var indexPosition = start; indexPosition < end; indexPosition++)
                {
                    var type = index[indexPosition];
                    if (!MatchesTypeKind(index, indexPosition, mode.TypeKind))
                        continue;

                    if (normalizedType.Length > 0
                        && !MatchesTypeName(index, indexPosition, typeNameQuery))
                        continue;

                    if (normalizedMember.Length > 0
                        && !(declaringTypes?.Contains(type)
                             ?? TypeDeclaresMatchingMember(type, mode.MemberKind, normalizedMember)))
                        continue;

                    count++;
                    if (destination.Count < MaxTypeRows)
                        destination.Add(type);
                }
            }

            // filtering the sorted type index preserves its deterministic output order.
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendLoadWarning(builder, loadWarning);
            if (totalCount == 0)
            {
                builder.Append("No types matched.");
                return BridgeCommandResult.Success(builder.ToTrimmedString());
            }

            if (totalCount > MaxTypeRows)
                AppendHeader(builder, "Types", totalCount, MaxTypeRows);

            for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
                AppendType(builder, matches[matchIndex]);

            AppendTruncation(builder, totalCount, MaxTypeRows, "types");
            return BridgeCommandResult.Success(builder.ToTrimmedString());
        }

        static BridgeCommandResult SearchMembers(
            IReadOnlyList<Type> index,
            ReflectMode mode,
            string typeQuery,
            string memberQuery,
            string loadWarning
        )
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = NormalizeQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Length == 0)
                return BridgeCommandResult.Error(
                    "reflect member modes require `type` or `member`. Examples: "
                    + "reflect(\"members\", type: \"Camera\") or reflect(\"methods\", member: \"Awake\")."
                );

            if (normalizedType.Length > 0)
                return SearchSpecificTypeMembers(index, mode, normalizedType, normalizedMember, loadWarning);

            return SearchWideMembers(index, mode, normalizedMember, loadWarning);
        }

        static BridgeCommandResult SearchSpecificTypeMembers(
            IReadOnlyList<Type> index,
            ReflectMode mode,
            string typeQuery,
            string memberQuery,
            string loadWarning
        )
        {
            var match = MatchSingleType(index, typeQuery);
            if (match.Kind == TypeMatchKind.None)
                return BridgeCommandResult.Error($"No type matched '{typeQuery}'.");

            if (match.Kind == TypeMatchKind.Ambiguous)
                return BridgeCommandResult.Ambiguous(TypeCandidates(
                    $"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.",
                    match.Candidates,
                    match.CandidateCount
                ));

            var target = match.Type!;
            var normalizedMember = NormalizeQuery(memberQuery);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendLoadWarning(builder, loadWarning);
            builder.AppendLine(
                $"Members for {ReflectionTypeFormatter.FormatType(target, includeNamespace: true)}"
            );
            builder.AppendLine($"Assembly: {GetAssemblyName(target.Assembly)}");
            AppendTypeHierarchy(builder, target);
            builder.AppendLine();

            var appendedAny = AppendTypeScopedMembers(builder, target, mode.MemberKind, normalizedMember);

            if (!appendedAny)
                builder.Append("No members matched.");

            return BridgeCommandResult.Success(builder.ToTrimmedString());
        }

        static BridgeCommandResult SearchWideMembers(
            IReadOnlyList<Type> index,
            ReflectMode mode,
            string memberQuery,
            string loadWarning
        )
        {
            var collector = new WideMemberCollector();
            AppendWideMemberMatches(collector, index, mode.MemberKind, memberQuery);

            var matches = collector.GetSortedMatches();
            var members = new List<MemberDisplay>(matches.Count);
            foreach (var match in matches)
                members.Add(ReflectionMemberFormatter.FormatMemberMatch(match));

            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendLoadWarning(builder, loadWarning);
            if (collector.TotalCount == 0)
            {
                builder.Append("No members matched.");
                return BridgeCommandResult.Success(builder.ToTrimmedString());
            }

            if (collector.TotalCount > MaxWideMemberRows)
                AppendHeader(builder, "Members", collector.TotalCount, MaxWideMemberRows);

            Type? currentType = null;
            ReflectMemberKind currentKind = ReflectMemberKind.None;
            var compatibilityDisplay = GetDetourCompatibilityDisplay(members);
            var shown = 0;
            foreach (var member in members)
            {
                if (currentType != member.DeclaringType)
                {
                    if (shown > 0)
                        builder.AppendLine();

                    currentType = member.DeclaringType;
                    currentKind = ReflectMemberKind.None;
                    builder.Append("Containing Type: ");
                    builder.Append(
                        ReflectionTypeFormatter.FormatType(
                            currentType,
                            includeNamespace: true
                        )
                    );
                    builder.Append(" (");
                    builder.Append(GetAssemblyName(currentType.Assembly));
                    builder.AppendLine(")");
                }

                if (currentKind != member.Kind)
                {
                    currentKind = member.Kind;
                    AppendMemberKindHeader(builder, currentKind, ref compatibilityDisplay);
                }

                AppendMember(builder, member, compatibilityDisplay);
                shown++;
            }

            AppendTruncation(builder, collector.TotalCount, MaxWideMemberRows, "members");
            return BridgeCommandResult.Success(builder.ToTrimmedString());
        }

    }
}
