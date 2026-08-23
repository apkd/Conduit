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
            if (!TryParseMode(mode, out var queryMode))
                throw new InvalidOperationException(InvalidModeDiagnostic(mode));

            ValidateResultType<T>(queryMode);
            var index = reflect.LoadIndexForHelpers();
            return queryMode.Category == ReflectCategory.Types
                ? FindTypes<T>(index, queryMode, type, member)
                : FindMembers<T>(index, queryMode, type, member);
        }

        static T FindOneCore<T>(string mode, string? type, string? member) where T : class
        {
            if (!TryParseMode(mode, out var queryMode))
                throw new InvalidOperationException(InvalidModeDiagnostic(mode));

            ValidateResultType<T>(queryMode);
            var index = reflect.LoadIndexForHelpers();
            var normalizedType = NormalizeQuery(type);
            if (queryMode.Category == ReflectCategory.Types && normalizedType.Length > 0)
                return FindSingleType<T>(index, queryMode, normalizedType, member);

            return SelectSingle(mode, type, member, queryMode.Category == ReflectCategory.Types
                ? FindTypes<T>(index, queryMode, type, member)
                : FindMembers<T>(index, queryMode, type, member));
        }

        static T FindSingleType<T>(IReadOnlyList<Type> index, ReflectMode mode, string typeQuery, string? memberQuery) where T : class
        {
            var normalizedMember = NormalizeQuery(memberQuery);
            // singular type lookup keeps the report tool's exact-name precedence before substring matches.
            var match = reflect.MatchSingleType(index, typeQuery, mode.TypeKind);

            if (match.Kind == TypeMatchKind.Ambiguous)
                throw new InvalidOperationException(TypeCandidates(
                    $"Multiple reflected results match {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.",
                    match.Candidates,
                    match.CandidateCount
                ));

            if (match.Kind == TypeMatchKind.None)
                throw new InvalidOperationException($"No reflected result matched {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.");

            var type = match.Type!;
            if (normalizedMember.Length > 0
                && !TypeDeclaresMatchingMember(type, ReflectMemberKind.None, normalizedMember))
                throw new InvalidOperationException($"No reflected result matched {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.");

            return (T)(object)type;
        }

        static T[] FindTypes<T>(IReadOnlyList<Type> index, ReflectMode mode, string? typeQuery, string? memberQuery) where T : class
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = NormalizeQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Length == 0)
                throw new InvalidOperationException("reflect type modes require `type` or `member`.");

            var typeNameQuery = new TypeNameQuery(normalizedType);
            var declaringTypes = normalizedType.Length == 0 && normalizedMember.Length > 0
                ? reflect.FindTypesDeclaringMatchingMember(index, normalizedMember)
                : null;
            var matches = new List<Type>();
            var workerCount = reflect.GetParallelScanWorkerCount(index.Count);
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
                    if (!reflect.MatchesTypeKind(index, position, mode.TypeKind))
                        continue;

                    if (normalizedType.Length > 0
                        && !reflect.MatchesTypeName(index, position, typeNameQuery))
                        continue;

                    if (normalizedMember.Length > 0
                        && !(declaringTypes?.Contains(type)
                             ?? TypeDeclaresMatchingMember(type, ReflectMemberKind.None, normalizedMember)))
                        continue;

                    destination.Add(type);
                }
            }
        }

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
            var match = reflect.MatchSingleType(index, typeQuery);
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
            var includeAccessors = reflect.IsAccessorQuery(memberQuery);
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
                var members = reflect.GetWideMemberIndex(index, memberKind, accessorsOnly);
                var segments = members.Segments;
                var entryCount = 0;
                foreach (var segment in segments)
                    entryCount += segment.Entries.Length;

                var workerCount = reflect.GetParallelScanWorkerCount(entryCount);
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
                    merged.Add(reflect.CompareWideMemberEntries(
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
                        if (nextType == null || reflect.CompareTypes(declaringType, nextType) < 0)
                            nextType = declaringType;
                    }

                    for (var sourceIndex = 0; sourceIndex < sources.Length; ++sourceIndex)
                    {
                        var source = sources[sourceIndex];
                        while (positions[sourceIndex] < source.Count)
                        {
                            var entry = source[positions[sourceIndex]];
                            if (!ReferenceEquals(entry.DeclaringType, nextType)
                                && reflect.CompareTypes(entry.DeclaringType, nextType) != 0)
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

        static void ValidateResultType<T>(ReflectMode mode) where T : class
        {
            var requestedType = typeof(T);
            if (mode.Category == ReflectCategory.Types)
            {
                if (requestedType == typeof(Type))
                    return;

                throw new InvalidOperationException($"Reflect mode '{FormatMode(mode)}' returns Type; requested {requestedType.Name}.");
            }

            if (!typeof(MemberInfo).IsAssignableFrom(requestedType))
                throw new InvalidOperationException($"Reflect mode '{FormatMode(mode)}' returns MemberInfo; requested {requestedType.Name}.");

            var requestedKind = GetRequestedMemberKind(requestedType);
            if (requestedKind == ReflectMemberKind.None
                || mode.MemberKind == ReflectMemberKind.None
                || mode.MemberKind == requestedKind)
                return;

            throw new InvalidOperationException($"Reflect mode '{FormatMode(mode)}' cannot return {requestedType.Name}.");
        }

        static ReflectMemberKind GetEffectiveMemberKind<T>(ReflectMemberKind modeKind) where T : class
        {
            if (modeKind != ReflectMemberKind.None)
                return modeKind;

            return GetRequestedMemberKind(typeof(T));
        }

        static ReflectMemberKind GetRequestedMemberKind(Type requestedType)
        {
            if (requestedType == typeof(FieldInfo))
                return ReflectMemberKind.Field;
            if (requestedType == typeof(PropertyInfo))
                return ReflectMemberKind.Property;
            if (requestedType == typeof(MethodInfo))
                return ReflectMemberKind.Method;
            if (requestedType == typeof(ConstructorInfo))
                return ReflectMemberKind.Constructor;
            if (requestedType == typeof(MemberInfo))
                return ReflectMemberKind.None;

            throw new InvalidOperationException($"Reflect member lookup does not support result type {requestedType.Name}.");
        }

        static T SelectSingle<T>(string mode, string? type, string? member, IReadOnlyList<T> matches) where T : class
        {
            if (matches.Count == 1)
                return matches[0];

            var query = FormatQuery(mode, type, member);
            if (matches.Count == 0)
                throw new InvalidOperationException($"No reflected result matched {query}.");

            throw new InvalidOperationException(FormatResultCandidates($"Multiple reflected results match {query}.", matches));
        }

        static T[] CastResults<T, TSource>(IReadOnlyList<TSource> matches)
            where T : class
            where TSource : class
        {
            if (matches.Count == 0)
                return Array.Empty<T>();

            var results = new T[matches.Count];
            for (var index = 0; index < matches.Count; index++)
                results[index] = (T)(object)matches[index];

            return results;
        }

        static bool TryParseMode(string mode, out ReflectMode queryMode)
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

        static bool TypeDeclaresMatchingMember(Type type, ReflectMemberKind kind, string memberQuery)
            => reflect.TypeDeclaresMatchingMember(type, kind, memberQuery);

        static FieldInfo[] GetFields(Type type)
            => reflect.GetFields(type);

        static PropertyInfo[] GetProperties(Type type)
            => reflect.GetProperties(type);

        static MethodInfo[] GetMethods(Type type, string memberQuery) => reflect.GetMethods(type, memberQuery);

        static ConstructorInfo[] GetConstructors(Type type)
            => reflect.GetConstructors(type);

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
            => MemberMatchRank(
                member.Name,
                member.DeclaringType ?? typeof(object),
                member is ConstructorInfo,
                query
            );

        static int MemberMatchRank(
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
                return ConstructorMatchRank(declaringType, query);

            return int.MaxValue;
        }

        static int ConstructorMatchRank(Type declaringType, string query)
        {
            if (string.Equals(query, "ctor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(query, ".ctor", StringComparison.OrdinalIgnoreCase))
                return 0;

            var shortName = ShortTypeName(declaringType);
            return TextMatchRank(shortName, query);
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

        static string DisplayTypeName(Type type, bool includeNamespace)
        {
            if (!type.IsGenericType)
                return PlainTypeName(type, includeNamespace);

            var definitionName = PlainTypeName(type, includeNamespace);
            var tick = definitionName.IndexOf('`');
            if (tick >= 0)
                definitionName = definitionName[..tick];

            var arguments = type.GetGenericArguments();
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append(definitionName);
            builder.Append('<');
            for (var index = 0; index < arguments.Length; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                builder.Append(DisplayTypeName(arguments[index], includeNamespace));
            }

            builder.Append('>');
            return builder.ToString();
        }

        static string PlainTypeName(Type type, bool includeNamespace)
        {
            var name = includeNamespace
                ? reflect.GetFullTypeName(type)
                : reflect.GetTypeName(type);

            return name.Replace('+', '.');
        }

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
            => reflect.CompareTypes(left, right);

        static ReflectMemberKind GetMemberKind(MemberInfo member)
            => member switch
            {
                FieldInfo       => ReflectMemberKind.Field,
                PropertyInfo    => ReflectMemberKind.Property,
                MethodInfo      => ReflectMemberKind.Method,
                ConstructorInfo => ReflectMemberKind.Constructor,
                _               => ReflectMemberKind.None,
            };

        static string FormatResultCandidates<T>(string header, IReadOnlyList<T> candidates) where T : class
            => FormatResultCandidates(header, candidates, candidates.Count);

        static string FormatResultCandidates<T>(
            string header,
            IReadOnlyList<T> candidates,
            int candidateCount) where T : class
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
                builder.AppendLine("- " + FormatCandidate(candidates[index]));

            AppendTruncation(builder, candidateCount, MaxCandidates, "candidates");
            return Trimmed(builder);
        }

        static string FormatCandidate(object candidate)
            => candidate switch
            {
                Type type         => $"{DisplayTypeName(type, includeNamespace: true)}, {type.Assembly.GetName().Name}",
                MemberInfo member => $"{DisplayTypeName(member.DeclaringType ?? typeof(object), includeNamespace: true)}.{member.Name} | {member.MemberType} | {member.Module.Assembly.GetName().Name}",
                _                 => candidate.ToString() ?? string.Empty,
            };

        static string TypeCandidates(
            string header,
            IReadOnlyList<Type> candidates,
            int candidateCount)
            => FormatResultCandidates(header, candidates, candidateCount);

        static void AppendTruncation(System.Text.StringBuilder builder, int count, int maxRows, string label)
        {
            if (count <= maxRows)
                return;

            builder.AppendLine();
            builder.Append("Truncated: ");
            builder.Append((count - maxRows).ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(label);
            builder.AppendLine(" omitted. Narrow the query.");
        }

        static string FormatQuery(string mode, string? type, string? member)
        {
            var normalizedType = NormalizeQuery(type);
            var normalizedMember = NormalizeQuery(member);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.Append("reflect query mode='");
            builder.Append(NormalizeQuery(mode));
            builder.Append('\'');
            if (normalizedType.Length > 0)
            {
                builder.Append(", type='");
                builder.Append(normalizedType);
                builder.Append('\'');
            }

            if (normalizedMember.Length > 0)
            {
                builder.Append(", member='");
                builder.Append(normalizedMember);
                builder.Append('\'');
            }

            return builder.ToString();
        }

        static string FormatMode(ReflectMode mode)
            => mode.Category == ReflectCategory.Types
                ? mode.TypeKind switch
                {
                    ReflectTypeKind.Class     => "classes",
                    ReflectTypeKind.Struct    => "structs",
                    ReflectTypeKind.Enum      => "enums",
                    ReflectTypeKind.Interface => "interfaces",
                    ReflectTypeKind.Delegate  => "delegates",
                    _                         => "types",
                }
                : mode.MemberKind switch
                {
                    ReflectMemberKind.Field       => "fields",
                    ReflectMemberKind.Property    => "properties",
                    ReflectMemberKind.Method      => "methods",
                    ReflectMemberKind.Constructor => "constructors",
                    _                             => "members",
                };

        static string InvalidModeDiagnostic(string mode)
            => $"Unsupported reflect mode '{mode}'. Valid modes: {ValidModes}.";

        static string NormalizeQuery(string? value)
            => value?.Trim() ?? string.Empty;

        static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static string ShortTypeName(Type type) => reflect.GetShortTypeName(type);

        static string Trimmed(StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[builder.Length - 1]))
                builder.Length--;

            return builder.ToString();
        }
    }
}
