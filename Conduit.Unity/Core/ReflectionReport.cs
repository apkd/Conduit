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
    static class reflect
    {
        const int MaxTypeRows = 100;
        const int MaxWideMemberRows = 200;
        const int MaxCandidates = 25;
        const int ParallelScanEntriesPerWorker = 2048;
        const int MaxParallelScanWorkers = 16;
        const BindingFlags DeclaredMembers = BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        const string ValidModes = "types, classes, structs, enums, interfaces, delegates, members, fields, properties, methods, constructors";
        static readonly object IndexLock = new();
        static readonly ConcurrentDictionary<Type, FieldInfo[]> fieldCache = new();
        static readonly ConcurrentDictionary<Type, PropertyInfo[]> propertyCache = new();
        static readonly ConcurrentDictionary<Type, MethodInfo[]> methodCache = new();
        static readonly ConcurrentDictionary<Type, MethodInfo[]> methodWithoutAccessorsCache = new();
        static readonly ConcurrentDictionary<Type, ConstructorInfo[]> constructorCache = new();
        static readonly ConcurrentDictionary<Assembly, string> assemblyNameCache = new();
        static readonly ConcurrentDictionary<Type, TypeSearchInfo> typeSearchInfoCache = new();

        // formatted declarations are immutable for the domain and recur across reflect calls.
        static readonly ConcurrentDictionary<MemberInfo, string> memberSignatureCache = new();
        static TypeIndex? cachedIndex;
        static List<Type>? pendingLoadedTypes;
        static Dictionary<string, TypeResolution>? exactTypeLookup;
        static WideMemberIndex? wideFieldIndex;
        static WideMemberIndex? widePropertyIndex;
        static WideMemberIndex? wideMethodIndex;
        static WideMemberIndex? wideAccessorIndex;
        static WideMemberIndex? wideConstructorIndex;
        static string cachedLoadWarning = string.Empty;

        static reflect()
            => AppDomain.CurrentDomain.AssemblyLoad += static (_, args) => AddLoadedAssembly(args.LoadedAssembly);

        static void AddLoadedAssembly(Assembly assembly)
        {
            lock (IndexLock)
                if (cachedIndex == null)
                    return;

            var addedTypes = new List<Type>();
            using var pooledWarnings = BridgeStringBuilderPool.Rent(out var addedWarnings);
            AppendAssemblyTypes(addedTypes, assembly, addedWarnings);

            lock (IndexLock)
            {
                if (cachedIndex == null)
                    return;

                if (addedTypes.Count > 0)
                    (pendingLoadedTypes ??= new()).AddRange(addedTypes);

                if (Trimmed(addedWarnings) is { Length: > 0 } warning)
                    AppendLoadWarning(warning);
            }
        }

        static void ApplyPendingLoadedTypes()
        {
            while (pendingLoadedTypes is { Count: > 0 } addedTypes)
            {
                pendingLoadedTypes = null;
                SortTypes(addedTypes);
                cachedIndex = MergeTypes(cachedIndex!, addedTypes);
                if (exactTypeLookup != null)
                    AddExactTypes(exactTypeLookup, addedTypes);
                ExtendWideMemberIndexes(addedTypes, cachedIndex);
            }
        }

        static void AppendLoadWarning(string warning)
        {
            var detailOffset = warning.IndexOf('\n');
            var detail = cachedLoadWarning.Length > 0 && detailOffset >= 0
                ? warning[(detailOffset + 1)..]
                : warning;
            cachedLoadWarning = cachedLoadWarning.Length == 0
                ? detail
                : cachedLoadWarning + "\n" + detail;
        }

        public static BridgeCommandResult Reflect(string[] args)
        {
            var mode = args.Length > 0 ? args[0] : string.Empty;
            var type = args.Length > 1 ? args[1] : string.Empty;
            var member = args.Length > 2 ? args[2] : string.Empty;

            if (!TryParseMode(mode, out var queryMode))
                return Error(InvalidModeDiagnostic(mode));

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
                return Error("reflect type modes require `type` or `member`. Examples: reflect(\"types\", type: \"Camera\") or reflect(\"types\", member: \"Awake\").");

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
                return Success(Trimmed(builder));
            }

            if (totalCount > MaxTypeRows)
                AppendHeader(builder, "Types", totalCount, MaxTypeRows);

            for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
                AppendType(builder, matches[matchIndex]);

            AppendTruncation(builder, totalCount, MaxTypeRows, "types");
            return Success(Trimmed(builder));
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
                return Error("reflect member modes require `type` or `member`. Examples: reflect(\"members\", type: \"Camera\") or reflect(\"methods\", member: \"Awake\").");

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
                return Error($"No type matched '{typeQuery}'.");

            if (match.Kind == TypeMatchKind.Ambiguous)
                return Ambiguous(TypeCandidates(
                    $"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.",
                    match.Candidates,
                    match.CandidateCount
                ));

            var target = match.Type!;
            var normalizedMember = NormalizeQuery(memberQuery);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendLoadWarning(builder, loadWarning);
            builder.AppendLine($"Members for {FormatType(target, includeNamespace: true)}");
            builder.AppendLine($"Assembly: {GetAssemblyName(target.Assembly)}");
            AppendTypeHierarchy(builder, target);
            builder.AppendLine();

            var appendedAny = AppendTypeScopedMembers(builder, target, mode.MemberKind, normalizedMember);

            if (!appendedAny)
                builder.Append("No members matched.");

            return Success(Trimmed(builder));
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

            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendLoadWarning(builder, loadWarning);
            if (collector.TotalCount == 0)
            {
                builder.Append("No members matched.");
                return Success(Trimmed(builder));
            }

            if (collector.TotalCount > MaxWideMemberRows)
                AppendHeader(builder, "Members", collector.TotalCount, MaxWideMemberRows);

            Type? currentType = null;
            ReflectMemberKind currentKind = ReflectMemberKind.None;
            var shown = 0;
            foreach (var match in matches)
            {
                var member = FormatMemberMatch(match);
                if (currentType != member.DeclaringType)
                {
                    if (shown > 0)
                        builder.AppendLine();

                    currentType = member.DeclaringType;
                    currentKind = ReflectMemberKind.None;
                    builder.Append("Containing Type: ");
                    builder.Append(FormatType(currentType, includeNamespace: true));
                    builder.Append(" (");
                    builder.Append(GetAssemblyName(currentType.Assembly));
                    builder.AppendLine(")");
                }

                if (currentKind != member.Kind)
                {
                    currentKind = member.Kind;
                    builder.Append("  ");
                    builder.Append(MemberKindHeader(currentKind));
                    builder.AppendLine(":");
                }

                builder.Append("  - ");
                builder.AppendLine(member.Signature);
                shown++;
            }

            AppendTruncation(builder, collector.TotalCount, MaxWideMemberRows, "members");
            return Success(Trimmed(builder));
        }

        static bool AppendTypeScopedMembers(StringBuilder builder, Type target, ReflectMemberKind kind, string memberQuery)
        {
            var appendedAny = false;
            AppendContainer(target, $"Declared on {FormatType(target, includeNamespace: true)}");

            for (var baseType = target.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
                AppendContainer(baseType, $"Inherited from {FormatType(baseType, includeNamespace: true)}");

            var interfaces = target.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);
            foreach (var interfaceType in interfaces)
                AppendContainer(interfaceType, $"Interface {FormatType(interfaceType, includeNamespace: true)}");

            return appendedAny;

            void AppendContainer(Type containingType, string label)
            {
                var members = GetDisplayMembers(containingType, kind, memberQuery);
                if (members.Count == 0)
                    return;

                if (appendedAny)
                    builder.AppendLine();

                builder.AppendLine($"{label} ({GetAssemblyName(containingType.Assembly)})");
                AppendMembersByKind(builder, members, null);
                appendedAny = true;
            }
        }

        static void AppendHeader(StringBuilder builder, string title, int total, int shown)
            => builder.AppendLine($"{title}: {total} {(total == 1 ? "match" : "matches")}; showing {shown}.");

        static void AppendLoadWarning(StringBuilder builder, string loadWarning)
        {
            if (string.IsNullOrWhiteSpace(loadWarning))
                return;

            builder.AppendLine(loadWarning);
            builder.AppendLine();
        }

        static void AppendType(StringBuilder builder, Type type)
        {
            var kind = TypeKindLabel(type);
            var baseType = type.BaseType == null || type.IsInterface
                ? "none"
                : FormatType(type.BaseType, includeNamespace: true);
            var interfaces = type.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);

            builder.Append("- ");
            builder.Append(kind);
            builder.Append(' ');
            builder.Append(FormatType(type, includeNamespace: true));
            builder.Append(" | Assembly: ");
            builder.Append(GetAssemblyName(type.Assembly));
            builder.Append(" | Base: ");
            builder.Append(baseType);
            builder.Append(" | Interfaces: ");
            builder.Append(interfaces.Length == 0 ? "none" : JoinTypes(interfaces, 8));
            builder.Append(" | Members: ");
            AppendMemberCounts(builder, type);
            builder.AppendLine();
        }

        static void AppendTypeHierarchy(StringBuilder builder, Type type)
        {
            builder.Append("Base: ");
            builder.AppendLine(type.BaseType == null || type.IsInterface ? "none" : FormatType(type.BaseType, includeNamespace: true));

            var interfaces = type.GetInterfaces();
            Array.Sort(interfaces, CompareTypes);
            builder.Append("Interfaces: ");
            builder.AppendLine(interfaces.Length == 0 ? "none" : JoinTypes(interfaces, 12));
        }

        static void AppendMemberCounts(StringBuilder builder, Type type)
        {
            builder.Append("fields=");
            AppendInvariant(builder, GetFields(type).Length);
            builder.Append(", properties=");
            AppendInvariant(builder, GetProperties(type).Length);
            builder.Append(", methods=");
            AppendInvariant(builder, GetMethods(type).Length);
            builder.Append(", constructors=");
            AppendInvariant(builder, GetConstructors(type).Length);
        }

        static void AppendInvariant(StringBuilder builder, int value)
        {
            Span<char> buffer = stackalloc char[11];
            value.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture);
            builder.Append(buffer[..written]);
        }

        static void AppendMembersByKind(StringBuilder builder, List<MemberDisplay> members, int? maxRows)
        {
            members.Sort(CompareMembers);
            var currentKind = ReflectMemberKind.None;
            var appended = 0;
            foreach (var member in members)
            {
                if (maxRows is { } limit && appended == limit)
                    break;

                if (currentKind != member.Kind)
                {
                    currentKind = member.Kind;
                    builder.Append("  ");
                    builder.Append(MemberKindHeader(currentKind));
                    builder.AppendLine(":");
                }

                builder.Append("  - ");
                builder.AppendLine(member.Signature);
                appended++;
            }
        }

        static void AppendTruncation(StringBuilder builder, int count, int maxRows, string label)
        {
            if (count <= maxRows)
                return;

            builder.AppendLine();
            builder.Append("Truncated: ");
            AppendInvariant(builder, count - maxRows);
            builder.Append(' ');
            builder.Append(label);
            builder.AppendLine(" omitted. Narrow the query.");
        }

        static List<MemberDisplay> GetDisplayMembers(Type type, ReflectMemberKind kind, string memberQuery)
        {
            var members = new List<MemberDisplay>();
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                foreach (var field in GetFields(type))
                    if (TryGetMemberMatchRank(field, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Field, field.Name, FormatMemberSignature(field), rank));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                foreach (var property in GetProperties(type))
                    if (TryGetMemberMatchRank(property, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Property, property.Name, FormatMemberSignature(property), rank));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
                foreach (var method in GetMethods(type, memberQuery))
                    if (TryGetMemberMatchRank(method, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Method, method.Name, FormatMemberSignature(method), rank));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                foreach (var constructor in GetConstructors(type))
                    if (TryGetMemberMatchRank(constructor, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Constructor, constructor.Name, FormatMemberSignature(constructor), rank));

            return members;
        }

        static void AppendWideMemberMatches(
            WideMemberCollector matches,
            IReadOnlyList<Type> types,
            ReflectMemberKind kind,
            string memberQuery)
        {
            var includeAccessors = IsAccessorQuery(memberQuery);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Field)
                Append(GetWideMemberIndex(types, ReflectMemberKind.Field), ReflectMemberKind.Field);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                Append(GetWideMemberIndex(types, ReflectMemberKind.Property), ReflectMemberKind.Property);
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
            {
                Append(GetWideMemberIndex(types, ReflectMemberKind.Method), ReflectMemberKind.Method);
                if (includeAccessors)
                    Append(
                        GetWideMemberIndex(types, ReflectMemberKind.Method, accessorsOnly: true),
                        ReflectMemberKind.Method
                    );
            }
            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                Append(GetWideMemberIndex(types, ReflectMemberKind.Constructor), ReflectMemberKind.Constructor);

            void Append(WideMemberIndex members, ReflectMemberKind memberKind)
            {
                var segments = members.Segments;
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

                var workerResults = new WideMemberCollector[workerCount];
                var nextSegment = -1;
                Parallel.For(0, workerCount, workerIndex =>
                {
                    var localMatches = new WideMemberCollector();
                    int segmentIndex;
                    while ((segmentIndex = Interlocked.Increment(ref nextSegment)) < segments.Length)
                        AppendSegment(localMatches, segments[segmentIndex]);

                    workerResults[workerIndex] = localMatches;
                });

                foreach (var workerResult in workerResults)
                    workerResult.MergeInto(matches);

                void AppendSegment(WideMemberCollector destination, WideMemberIndexSegment segment)
                {
                    foreach (var entry in segment.Entries)
                    {
                        if (TryGetMemberMatchRank(
                                entry.Name,
                                entry.DeclaringType,
                                memberKind == ReflectMemberKind.Constructor,
                                memberQuery,
                                out var rank
                            ))
                            destination.Add(new(
                                entry.DeclaringType,
                                memberKind,
                                entry.Name,
                                entry.Member,
                                rank
                            ));
                    }
                }
            }
        }

        internal static WideMemberIndex GetWideMemberIndex(
            IReadOnlyList<Type> types,
            ReflectMemberKind kind,
            bool accessorsOnly = false)
        {
            lock (IndexLock)
            {
                while (true)
                {
                    if (TryGetCachedWideMemberIndex(kind, accessorsOnly, out var cached))
                        return cached;

                    var currentTypes = cachedIndex ?? types;
                    var built = new WideMemberIndex(
                        BuildWideMemberIndex(currentTypes, kind, accessorsOnly)
                    );
                    // reflecting metadata can load another assembly and replace the type index reentrantly.
                    if (!ReferenceEquals(currentTypes, cachedIndex))
                        continue;

                    SetCachedWideMemberIndex(kind, accessorsOnly, built);
                    return built;
                }
            }
        }

        static WideMemberIndexSegment[] BuildWideMemberIndex(
            IReadOnlyList<Type> types,
            ReflectMemberKind kind,
            bool accessorsOnly = false)
        {
            var segments = new List<WideMemberIndexSegment>();
            var members = new List<WideMemberIndexEntry>();
            string? currentAssemblyName = null;
            for (var position = 0; position < types.Count; position++)
            {
                var type = types[position];
                var assemblyName = GetTypeSearchInfo(types, position).AssemblyName;
                if (currentAssemblyName != assemblyName)
                {
                    FlushSegment();
                    currentAssemblyName = assemblyName;
                }

                var groupStart = members.Count;
                // the global index retains each member; avoid also retaining one cached array per loaded type.
                switch (kind)
                {
                    case ReflectMemberKind.Field:
                        AddWideMembers(
                            members,
                            type,
                            fieldCache.TryGetValue(type, out var fields)
                                ? fields
                                : type.GetFields(DeclaredMembers)
                        );
                        break;
                    case ReflectMemberKind.Property:
                        AddWideMembers(
                            members,
                            type,
                            propertyCache.TryGetValue(type, out var properties)
                                ? properties
                                : type.GetProperties(DeclaredMembers)
                        );
                        break;
                    case ReflectMemberKind.Method:
                        AddWideMethods(
                            members,
                            type,
                            methodCache.TryGetValue(type, out var methods)
                                ? methods
                                : type.GetMethods(DeclaredMembers),
                            accessorsOnly
                        );
                        break;
                    case ReflectMemberKind.Constructor:
                        AddWideMembers(
                            members,
                            type,
                            constructorCache.TryGetValue(type, out var constructors)
                                ? constructors
                                : type.GetConstructors(DeclaredMembers)
                        );
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind));
                }

                members.Sort(
                    groupStart,
                    members.Count - groupStart,
                    WideMemberWithinTypeComparer.Instance
                );
            }

            FlushSegment();
            return segments.ToArray();

            void FlushSegment()
            {
                if (members.Count == 0 || currentAssemblyName == null)
                    return;

                segments.Add(new(currentAssemblyName, members.ToArray()));
                members.Clear();
            }
        }

        static void AddWideMembers(
            List<WideMemberIndexEntry> members,
            Type declaringType,
            MemberInfo[] values)
        {
            foreach (var value in values)
                members.Add(new(declaringType, value));
        }

        static void AddWideMethods(
            List<WideMemberIndexEntry> members,
            Type declaringType,
            MethodInfo[] values,
            bool accessorsOnly)
        {
            foreach (var value in values)
                if (IsPropertyOrEventAccessor(value) == accessorsOnly)
                    members.Add(new(declaringType, value));
        }

        static bool TryGetCachedWideMemberIndex(
            ReflectMemberKind kind,
            bool accessorsOnly,
            out WideMemberIndex members)
        {
            WideMemberIndex? cached = (kind, accessorsOnly) switch
            {
                (ReflectMemberKind.Field, false) => wideFieldIndex,
                (ReflectMemberKind.Property, false) => widePropertyIndex,
                (ReflectMemberKind.Method, false) => wideMethodIndex,
                (ReflectMemberKind.Method, true) => wideAccessorIndex,
                (ReflectMemberKind.Constructor, false) => wideConstructorIndex,
                _ => null,
            };
            members = cached!;
            return cached != null;
        }

        static void SetCachedWideMemberIndex(
            ReflectMemberKind kind,
            bool accessorsOnly,
            WideMemberIndex members)
        {
            if (accessorsOnly)
            {
                wideAccessorIndex = members;
                return;
            }

            switch (kind)
            {
                case ReflectMemberKind.Field:
                    wideFieldIndex = members;
                    break;
                case ReflectMemberKind.Property:
                    widePropertyIndex = members;
                    break;
                case ReflectMemberKind.Method:
                    wideMethodIndex = members;
                    break;
                case ReflectMemberKind.Constructor:
                    wideConstructorIndex = members;
                    break;
            }
        }

        static void InvalidateWideMemberIndexes()
        {
            wideFieldIndex = null;
            widePropertyIndex = null;
            wideMethodIndex = null;
            wideAccessorIndex = null;
            wideConstructorIndex = null;
        }

        static void ExtendWideMemberIndexes(
            IReadOnlyList<Type> addedTypes,
            IReadOnlyList<Type> expectedTypeIndex)
        {
            if (wideFieldIndex == null
                && widePropertyIndex == null
                && wideMethodIndex == null
                && wideAccessorIndex == null
                && wideConstructorIndex == null)
                return;

            var fields = wideFieldIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Field);
            var properties = widePropertyIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Property);
            var methods = wideMethodIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Method);
            var accessors = wideAccessorIndex == null
                ? null
                : BuildWideMemberIndex(
                    addedTypes,
                    ReflectMemberKind.Method,
                    accessorsOnly: true
                );
            var constructors = wideConstructorIndex == null
                ? null
                : BuildWideMemberIndex(addedTypes, ReflectMemberKind.Constructor);

            // metadata inspection can load another assembly reentrantly; its handler owns the newer indexes.
            if (!ReferenceEquals(expectedTypeIndex, cachedIndex))
            {
                InvalidateWideMemberIndexes();
                return;
            }

            if (fields is { Length: > 0 })
                wideFieldIndex!.AddSegments(fields);
            if (properties is { Length: > 0 })
                widePropertyIndex!.AddSegments(properties);
            if (methods is { Length: > 0 })
                wideMethodIndex!.AddSegments(methods);
            if (accessors is { Length: > 0 })
                wideAccessorIndex!.AddSegments(accessors);
            if (constructors is { Length: > 0 })
                wideConstructorIndex!.AddSegments(constructors);
        }

        internal static int CompareWideMemberEntries(
            WideMemberIndexEntry left,
            WideMemberIndexEntry right)
        {
            var type = CompareTypes(left.DeclaringType, right.DeclaringType);
            if (type != 0)
                return type;

            var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return name != 0
                ? name
                : WideMemberIndexEntry.GetMetadataToken(left.Member)
                    .CompareTo(WideMemberIndexEntry.GetMetadataToken(right.Member));
        }

        static MemberDisplay FormatMemberMatch(MemberMatch match) =>
            new(
                match.DeclaringType,
                match.Kind,
                match.Name,
                FormatMemberSignature(match),
                match.MatchRank
            );

        static string FormatMemberSignature(MemberMatch match)
            => FormatMemberSignature(match.Member);

        static string FormatMemberSignature(MemberInfo member)
            => memberSignatureCache.GetOrAdd(member, static value => value switch
            {
                FieldInfo field             => FormatField(field),
                PropertyInfo property       => FormatProperty(property),
                MethodInfo method           => FormatMethod(method),
                ConstructorInfo constructor => FormatConstructor(constructor),
                _                           => value.ToString() ?? value.Name,
            });

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

            filtered = Array.FindAll(methods, static method => !IsPropertyOrEventAccessor(method));
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

        internal static TypeMatch MatchSingleType(
            IReadOnlyList<Type> index,
            string query,
            ReflectTypeKind kind = ReflectTypeKind.Any)
        {
            lock (IndexLock)
                if (ReferenceEquals(index, cachedIndex)
                    && GetExactTypeLookup().TryGetValue(query, out var indexed)
                    && indexed.Type != null
                    && MatchesTypeKind(indexed.Type, kind))
                    return TypeMatch.Matched(indexed.Type);

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
                    ? info.ShortDisplayName ??= DisplayTypeName(type, includeNamespace: false)
                    : null;
                var fullDisplayName = needsDisplayName
                    ? DisplayTypeName(type, includeNamespace: true)
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

        static IReadOnlyList<Type> LoadIndex(out string warning)
        {
            lock (IndexLock)
            {
                if (cachedIndex != null)
                {
                    // generated snippets are folded into the sorted index only when reflection needs them.
                    ApplyPendingLoadedTypes();
                    warning = cachedLoadWarning;
                    return cachedIndex;
                }

                cachedIndex = BuildIndex(out cachedLoadWarning);
                warning = cachedLoadWarning;
                return cachedIndex;
            }
        }

        internal static IReadOnlyList<Type> LoadIndexForHelpers()
            => LoadIndex(out _);

        internal static int GetParallelScanWorkerCount(int entryCount)
            => Math.Min(
                Math.Min(Environment.ProcessorCount, MaxParallelScanWorkers),
                Math.Max(1, entryCount / ParallelScanEntriesPerWorker)
            );

        static TypeIndex BuildIndex(out string warning)
        {
            var types = new List<Type>();
            using var pooledWarnings = BridgeStringBuilderPool.Rent(out var warnings);
            var assemblies = new HashSet<Assembly>();
            while (true)
            {
                var addedAssembly = false;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!assemblies.Add(assembly))
                        continue;

                    addedAssembly = true;
                    AppendAssemblyTypes(types, assembly, warnings);
                }

                // GetTypes can load dependencies reentrantly; include them before publishing the index.
                if (!addedAssembly)
                    break;
            }

            warning = Trimmed(warnings);
            return SortIndex(types);
        }

        static void AppendAssemblyTypes(List<Type> types, Assembly assembly, StringBuilder warnings)
        {
            try
            {
                types.AddRange(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                foreach (var type in exception.Types)
                    if (type != null)
                        types.Add(type);

                AppendLoadFailure(exception);
            }
            catch (Exception exception)
            {
                AppendLoadFailure(exception);
            }

            void AppendLoadFailure(Exception exception)
            {
                if (warnings.Length == 0)
                    warnings.AppendLine("Warning: some loaded assemblies could not be fully reflected.");
                else if (warnings[^1] != '\n')
                    warnings.AppendLine();

                warnings.Append("- ")
                    .Append(assembly.GetName().Name)
                    .Append(": ")
                    .AppendLine(exception.Message);
            }
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

        internal static bool MatchesTypeKind(Type type, ReflectTypeKind kind)
            => kind == ReflectTypeKind.Any || GetTypeSearchInfo(type).Kind == kind;

        internal static bool MatchesTypeKind(
            IReadOnlyList<Type> index,
            int position,
            ReflectTypeKind kind
        ) => kind == ReflectTypeKind.Any || GetTypeSearchInfo(index, position).Kind == kind;

        static string FormatField(FieldInfo field)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendFieldAccess(builder, field);
            AppendFieldModifiers(builder, field);
            builder.Append(FormatType(field.FieldType));
            builder.Append(' ');
            builder.Append(CSharpIdentifier.Escape(field.Name));
            return builder.ToString();
        }

        static string FormatProperty(PropertyInfo property)
        {
            var accessor = PrimaryAccessor(property);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            if (accessor != null)
            {
                AppendAccess(builder, accessor);
                if (accessor.IsStatic)
                    builder.Append("static ");
            }

            if (RequiresUnsafe(property.PropertyType)
                || Array.Exists(
                    property.GetIndexParameters(),
                    static parameter => RequiresUnsafe(parameter.ParameterType)
                ))
                builder.Append("unsafe ");

            var propertyType = property.PropertyType;
            if (propertyType.IsByRef)
            {
                builder.Append(property.GetMethod is { } getter && IsReadOnly(getter.ReturnParameter)
                    ? "ref readonly "
                    : "ref ");
                propertyType = propertyType.GetElementType() ?? propertyType;
            }

            builder.Append(FormatType(propertyType));
            builder.Append(' ');
            builder.Append(FormatPropertyName(property));
            builder.Append(" { ");
            AppendPropertyAccessor(builder, "get", property.GetMethod, accessor);
            if (property.GetMethod != null && property.SetMethod != null)
                builder.Append(' ');
            AppendPropertyAccessor(builder, IsInitOnly(property.SetMethod) ? "init" : "set", property.SetMethod, accessor);
            builder.Append(" }");
            return builder.ToString();
        }

        static MethodInfo? PrimaryAccessor(PropertyInfo property)
        {
            if (property.GetMethod == null)
                return property.SetMethod;
            if (property.SetMethod == null)
                return property.GetMethod;

            return AccessRank(property.GetMethod) >= AccessRank(property.SetMethod)
                ? property.GetMethod
                : property.SetMethod;
        }

        static string FormatPropertyName(PropertyInfo property)
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 0)
                return CSharpIdentifier.Escape(property.Name);

            return "this[" + FormatParameters(parameters) + "]";
        }

        static void AppendPropertyAccessor(StringBuilder builder, string name, MethodInfo? accessor, MethodInfo? primaryAccessor)
        {
            if (accessor == null)
                return;

            if (primaryAccessor != null && AccessRank(accessor) != AccessRank(primaryAccessor))
            {
                AppendAccess(builder, accessor, includePrivate: true);
            }

            builder.Append(name);
            builder.Append(';');
        }

        static string FormatMethod(MethodInfo method)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendAccess(builder, method);
            AppendMethodModifiers(builder, method);
            AppendReturnType(builder, method);
            builder.Append(' ');
            builder.Append(CSharpIdentifier.EscapeQualified(method.Name));
            AppendGenericArguments(builder, method.GetGenericArguments());
            builder.Append('(');
            builder.Append(FormatParameters(method.GetParameters()));
            builder.Append(')');
            AppendDetourCompatibility(builder, method);
            return builder.ToString();
        }

        static string FormatConstructor(ConstructorInfo constructor)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            if (constructor.IsStatic)
                builder.Append("static ");
            else
                AppendAccess(builder, constructor);

            builder.Append(constructor.DeclaringType == null ? ".ctor" : DisplayTypeName(constructor.DeclaringType, includeNamespace: false));
            builder.Append('(');
            builder.Append(FormatParameters(constructor.GetParameters()));
            builder.Append(')');
            AppendDetourCompatibility(builder, constructor);
            return builder.ToString();
        }

        static void AppendReturnType(StringBuilder builder, MethodInfo method)
        {
            var returnType = method.ReturnType;
            if (returnType.IsByRef)
            {
                builder.Append(IsReadOnly(method.ReturnParameter) ? "ref readonly " : "ref ");
                returnType = returnType.GetElementType() ?? returnType;
            }

            builder.Append(FormatType(returnType));
        }

        static void AppendDetourCompatibility(StringBuilder builder, MethodBase method)
        {
            if (MethodDetourSupport.GetUnsupportedReason(method) != null)
                builder.Append(" // detour-incompatible");
        }

        static string FormatParameters(ParameterInfo[] parameters)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            for (var index = 0; index < parameters.Length; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                AppendParameter(builder, parameters[index]);
            }

            return builder.ToString();
        }

        static void AppendParameter(StringBuilder builder, ParameterInfo parameter)
        {
            if (parameter.GetCustomAttribute<ParamArrayAttribute>() != null)
                builder.Append("params ");

            var parameterType = parameter.ParameterType;
            if (parameterType.IsByRef)
            {
                if (HasAttribute(parameter, "System.Runtime.CompilerServices.RequiresLocationAttribute"))
                    builder.Append("ref readonly ");
                else if (parameter.IsOut)
                    builder.Append("out ");
                else if (parameter.IsIn || IsReadOnly(parameter))
                    builder.Append("in ");
                else
                    builder.Append("ref ");

                parameterType = parameterType.GetElementType() ?? parameterType;
            }

            builder.Append(FormatType(parameterType));
            builder.Append(' ');
            builder.Append(CSharpIdentifier.Escape(parameter.Name ?? "arg"));
            if (parameter.HasDefaultValue)
            {
                builder.Append(" = ");
                builder.Append(FormatDefaultValue(parameter.DefaultValue));
            }
        }

        static bool IsReadOnly(ParameterInfo parameter)
            => HasAttribute(parameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute")
               || HasAttribute(parameter, "System.Runtime.InteropServices.InAttribute")
               || HasRequiredModifier(parameter, "System.Runtime.CompilerServices.IsReadOnlyAttribute")
               || HasRequiredModifier(parameter, "System.Runtime.InteropServices.InAttribute");

        static bool HasAttribute(ParameterInfo parameter, string fullName)
        {
            foreach (var attribute in parameter.GetCustomAttributesData())
                if (attribute.AttributeType.FullName == fullName)
                    return true;

            return false;
        }

        static bool HasRequiredModifier(ParameterInfo parameter, string fullName)
        {
            try
            {
                foreach (var modifier in parameter.GetRequiredCustomModifiers())
                    if (modifier.FullName == fullName)
                        return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException) { }

            return false;
        }

        static string FormatDefaultValue(object? value)
            => value switch
            {
                null        => "null",
                string text => "\"" + text.Replace("\"", "\\\"") + "\"",
                char c      => "'" + c + "'",
                bool b      => b ? "true" : "false",
                Enum e      => CSharpIdentifier.Escape(e.GetType().Name) + "." + e,
                _           => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            };

        static string FormatType(Type type, bool includeNamespace = false)
        {
            if (type.IsByRef)
                type = type.GetElementType() ?? type;

            if (MonoSignature.IsFunctionPointer(type))
                return MonoSignature.FormatFunctionPointer(type);

            if (type.IsPointer)
                return FormatType(type.GetElementType() ?? typeof(void), includeNamespace) + "*";

            if (TryBuiltInAlias(type, out var alias))
                return alias;

            if (type.IsArray)
                return FormatType(type.GetElementType() ?? typeof(object), includeNamespace) + "[" + new string(',', type.GetArrayRank() - 1) + "]";

            if (Nullable.GetUnderlyingType(type) is { } underlying)
                return FormatType(underlying, includeNamespace) + "?";

            if (type.IsGenericParameter)
                return type.Name;

            return DisplayTypeName(type, includeNamespace);
        }

        static string DisplayTypeName(Type type, bool includeNamespace)
        {
            if (!includeNamespace && !type.IsNested && !type.IsGenericType)
                return CSharpIdentifier.Escape(type.Name);

            var hierarchy = new Stack<Type>();
            for (var current = type; current != null; current = current.DeclaringType)
                hierarchy.Push(current);

            var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
            var argumentIndex = 0;
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            while (hierarchy.Count > 0)
            {
                var part = hierarchy.Pop();
                if (builder.Length == 0 && includeNamespace && !string.IsNullOrEmpty(part.Namespace))
                    builder.Append(CSharpIdentifier.EscapeQualified(part.Namespace)).Append('.');
                else if (builder.Length > 0)
                    builder.Append('.');

                var tick = part.Name.IndexOf('`');
                builder.Append(CSharpIdentifier.Escape(
                    tick < 0 ? part.Name : part.Name.Substring(0, tick)
                ));
                if (tick < 0)
                    continue;

                var arity = int.Parse(part.Name.Substring(tick + 1), CultureInfo.InvariantCulture);
                builder.Append('<');
                for (var index = 0; index < arity; index++)
                {
                    if (index > 0)
                        builder.Append(", ");

                    builder.Append(argumentIndex < arguments.Length
                        ? FormatType(arguments[argumentIndex++], includeNamespace)
                        : "?");
                }

                builder.Append('>');
            }

            return builder.ToString();
        }

        static bool TryBuiltInAlias(Type type, out string alias)
        {
            alias = type == typeof(void) ? "void"
                : type == typeof(bool) ? "bool"
                : type == typeof(byte) ? "byte"
                : type == typeof(sbyte) ? "sbyte"
                : type == typeof(char) ? "char"
                : type == typeof(decimal) ? "decimal"
                : type == typeof(double) ? "double"
                : type == typeof(float) ? "float"
                : type == typeof(int) ? "int"
                : type == typeof(uint) ? "uint"
                : type == typeof(long) ? "long"
                : type == typeof(ulong) ? "ulong"
                : type == typeof(object) ? "object"
                : type == typeof(short) ? "short"
                : type == typeof(ushort) ? "ushort"
                : type == typeof(string) ? "string"
                : string.Empty;
            return alias.Length > 0;
        }

        static void AppendFieldAccess(StringBuilder builder, FieldInfo field)
        {
            var access = Access(field, includePrivate: field.DeclaringType?.IsInterface == true);
            if (access.Length > 0)
                builder.Append(access).Append(' ');
        }

        static void AppendFieldModifiers(StringBuilder builder, FieldInfo field)
        {
            if (field.IsLiteral)
                builder.Append("const ");
            else
            {
                if (field.IsStatic)
                    builder.Append("static ");
                if (RequiresUnsafe(field.FieldType))
                    builder.Append("unsafe ");
                if (IsVolatile(field))
                    builder.Append("volatile ");
                else if (field.IsInitOnly)
                    builder.Append("readonly ");
            }
        }

        static void AppendMethodModifiers(StringBuilder builder, MethodInfo method)
        {
            if (method.IsStatic)
                builder.Append("static ");
            if (!method.IsStatic && method.DeclaringType?.IsValueType == true && HasMethodAttribute(method, "System.Runtime.CompilerServices.IsReadOnlyAttribute"))
                builder.Append("readonly ");
            if (RequiresUnsafe(method))
                builder.Append("unsafe ");

            var isInterface = method.DeclaringType?.IsInterface == true;
            var isOverride = IsOverride(method);
            if (isOverride)
            {
                if (method.IsFinal)
                    builder.Append("sealed ");
                builder.Append("override ");
            }
            else if (method.IsAbstract && (!isInterface || method.IsStatic))
                builder.Append("abstract ");
            else if (method.IsVirtual && !method.IsFinal && (!isInterface || method.IsStatic))
                builder.Append("virtual ");

            if (!method.IsAbstract && HasNoManagedBody(method))
                builder.Append("extern ");
        }

        static bool IsOverride(MethodInfo method)
        {
            try
            {
                return method.GetBaseDefinition() != method;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return false;
            }
        }

        static void AppendGenericArguments(StringBuilder builder, Type[] genericArguments)
        {
            if (genericArguments.Length == 0)
                return;

            builder.Append('<');
            for (var index = 0; index < genericArguments.Length; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                builder.Append(CSharpIdentifier.Escape(genericArguments[index].Name));
            }

            builder.Append('>');
        }

        static void AppendAccess(StringBuilder builder, MethodBase method, bool includePrivate = false)
        {
            var access = Access(method, includePrivate || method.DeclaringType?.IsInterface == true);
            if (access.Length > 0)
                builder.Append(access).Append(' ');
        }

        static string Access(MethodBase method, bool includePrivate)
        {
            if (method.IsPublic)
                return method.DeclaringType?.IsInterface == true ? string.Empty : "public";
            if (method.IsFamily)
                return "protected";
            if (method.IsFamilyOrAssembly)
                return "protected internal";
            if (method.IsFamilyAndAssembly)
                return "private protected";
            if (method.IsAssembly)
                return "internal";
            return includePrivate && !IsExplicitInterfaceImplementation(method) ? "private" : string.Empty;
        }

        static string Access(FieldInfo field, bool includePrivate)
        {
            if (field.IsPublic)
                return field.DeclaringType?.IsInterface == true ? string.Empty : "public";
            if (field.IsFamily)
                return "protected";
            if (field.IsFamilyOrAssembly)
                return "protected internal";
            if (field.IsFamilyAndAssembly)
                return "private protected";
            if (field.IsAssembly)
                return "internal";
            return includePrivate ? "private" : string.Empty;
        }

        static int AccessRank(MethodBase method)
        {
            if (method.IsPublic)
                return 5;
            if (method.IsFamilyOrAssembly)
                return 4;
            if (method.IsFamily || method.IsAssembly)
                return 3;
            if (method.IsFamilyAndAssembly)
                return 2;
            return 1;
        }

        static bool IsExplicitInterfaceImplementation(MethodBase method)
            => method.IsPrivate && method.IsFinal && method.IsVirtual && method.Name.IndexOf('.') >= 0;

        static bool HasMethodAttribute(MethodInfo method, string fullName)
        {
            foreach (var attribute in method.GetCustomAttributesData())
                if (attribute.AttributeType.FullName == fullName)
                    return true;

            return false;
        }

        static bool IsVolatile(FieldInfo field)
        {
            try
            {
                foreach (var modifier in field.GetRequiredCustomModifiers())
                    if (modifier.FullName == "System.Runtime.CompilerServices.IsVolatile")
                        return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException) { }

            return false;
        }

        static bool IsInitOnly(MethodInfo? setter)
            => setter != null && HasRequiredModifier(setter.ReturnParameter, "System.Runtime.CompilerServices.IsExternalInit");

        static bool HasNoManagedBody(MethodInfo method)
        {
            try
            {
                return method.GetMethodBody() == null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return true;
            }
        }

        static bool RequiresUnsafe(MethodInfo method)
        {
            if (RequiresUnsafe(method.ReturnType))
                return true;

            foreach (var parameter in method.GetParameters())
                if (RequiresUnsafe(parameter.ParameterType))
                    return true;

            return false;
        }

        static bool RequiresUnsafe(Type type)
        {
            if (type.IsByRef || type.IsArray)
                return RequiresUnsafe(type.GetElementType() ?? type);
            if (type.IsPointer || MonoSignature.IsFunctionPointer(type))
                return true;
            if (!type.IsGenericType)
                return false;

            foreach (var argument in type.GetGenericArguments())
                if (RequiresUnsafe(argument))
                    return true;

            return false;
        }

        internal static bool IsPropertyOrEventAccessor(MethodInfo method)
            => method.IsSpecialName
               && (method.Name.StartsWith("get_", StringComparison.Ordinal)
                   || method.Name.StartsWith("set_", StringComparison.Ordinal)
                   || method.Name.StartsWith("add_", StringComparison.Ordinal)
                   || method.Name.StartsWith("remove_", StringComparison.Ordinal)
                   || method.Name.StartsWith("raise_", StringComparison.Ordinal));

        static string JoinTypes(Type[] types, int max)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            var shown = Math.Min(types.Length, max);
            for (var index = 0; index < shown; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                builder.Append(FormatType(types[index], includeNamespace: true));
            }

            if (types.Length > shown)
            {
                builder.Append(", +");
                AppendInvariant(builder, types.Length - shown);
                builder.Append(" more");
            }

            return builder.ToString();
        }

        static string TypeKindLabel(Type type)
        {
            if (type.IsEnum)
                return "enum";
            if (type.IsInterface)
                return "interface";
            if (type.IsSubclassOf(typeof(MulticastDelegate)))
                return "delegate";
            if (type.IsValueType)
            {
                var readOnly = HasTypeAttribute(
                    type,
                    "System.Runtime.CompilerServices.IsReadOnlyAttribute"
                );
                if (type.IsByRefLike)
                    return readOnly ? "readonly ref struct" : "ref struct";
                return readOnly ? "readonly struct" : "struct";
            }
            return "class";
        }

        static bool HasTypeAttribute(Type type, string fullName)
        {
            foreach (var attribute in type.GetCustomAttributesData())
                if (attribute.AttributeType.FullName == fullName)
                    return true;

            return false;
        }

        static string MemberKindHeader(ReflectMemberKind kind)
            => kind switch
            {
                ReflectMemberKind.Field       => "Fields",
                ReflectMemberKind.Property    => "Properties",
                ReflectMemberKind.Method      => "Methods",
                ReflectMemberKind.Constructor => "Constructors",
                _                             => "Members",
            };

        static int CompareMembers(MemberDisplay left, MemberDisplay right)
        {
            var rank = left.MatchRank.CompareTo(right.MatchRank);
            if (rank != 0)
                return rank;

            var type = CompareTypes(left.DeclaringType, right.DeclaringType);
            if (type != 0)
                return type;

            var kind = left.Kind.CompareTo(right.Kind);
            if (kind != 0)
                return kind;

            var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return name != 0
                ? name
                : string.Compare(left.Signature, right.Signature, StringComparison.Ordinal);
        }

        static int CompareMemberMatches(MemberMatch left, MemberMatch right)
        {
            var rank = left.MatchRank.CompareTo(right.MatchRank);
            if (rank != 0)
                return rank;

            var type = CompareTypes(left.DeclaringType, right.DeclaringType);
            if (type != 0)
                return type;

            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        }

        static int CompareMemberMatchesWithSignatures(MemberMatch left, MemberMatch right)
        {
            var comparison = CompareMemberMatches(left, right);
            return comparison != 0
                ? comparison
                : string.Compare(
                    FormatMemberSignature(left),
                    FormatMemberSignature(right),
                    StringComparison.Ordinal
                );
        }

        sealed class WideMemberCollector
        {
            readonly List<MemberMatch> matches = new(MaxWideMemberRows);

            public int TotalCount { get; private set; }

            public void Add(MemberMatch match)
            {
                TotalCount++;
                AddCandidate(match);
            }

            public void MergeInto(WideMemberCollector destination)
            {
                destination.TotalCount += TotalCount;
                foreach (var match in matches)
                    destination.AddCandidate(match);
            }

            void AddCandidate(MemberMatch match)
            {
                if (matches.Count < MaxWideMemberRows)
                {
                    matches.Add(match);
                    if (matches.Count == MaxWideMemberRows)
                        BuildHeap();
                    return;
                }

                if (CompareMemberMatchesWithSignatures(match, matches[0]) >= 0)
                    return;

                matches[0] = match;
                SiftDown(0);
            }

            public List<MemberMatch> GetSortedMatches()
            {
                matches.Sort(CompareMemberMatchesWithSignatures);
                return matches;
            }

            void BuildHeap()
            {
                for (var index = matches.Count / 2 - 1; index >= 0; index--)
                    SiftDown(index);
            }

            void SiftDown(int index)
            {
                while (true)
                {
                    var left = index * 2 + 1;
                    if (left >= matches.Count)
                        return;

                    var right = left + 1;
                    var worse = right < matches.Count
                                && CompareMemberMatchesWithSignatures(matches[right], matches[left]) > 0
                        ? right
                        : left;
                    if (CompareMemberMatchesWithSignatures(matches[worse], matches[index]) <= 0)
                        return;

                    (matches[index], matches[worse]) = (matches[worse], matches[index]);
                    index = worse;
                }
            }
        }

        static void SortTypes(List<Type> types) => types.Sort(CompareTypes);

        static TypeIndex SortIndex(List<Type> types)
        {
            var entries = new TypeSortEntry[types.Count];
            for (var index = 0; index < types.Count; index++)
            {
                var type = types[index];
                var info = CreateTypeSearchInfo(type);
                entries[index] = new(
                    type,
                    info
                );
            }

            // precomputed keys avoid repeated Mono reflection and cache lookups in the O(n log n) sort.
            Array.Sort(entries, static (left, right) =>
            {
                var assembly = string.Compare(
                    left.SearchInfo.AssemblyName,
                    right.SearchInfo.AssemblyName,
                    StringComparison.Ordinal
                );
                return assembly != 0
                    ? assembly
                    : string.Compare(
                        left.SearchInfo.FullName,
                        right.SearchInfo.FullName,
                        StringComparison.Ordinal
                    );
            });

            var sortedTypes = new Type[entries.Length];
            var searchInfos = new TypeSearchInfo[entries.Length];
            for (var index = 0; index < entries.Length; index++)
            {
                sortedTypes[index] = entries[index].Type;
                searchInfos[index] = entries[index].SearchInfo;
            }

            return new(sortedTypes, searchInfos);
        }

        static TypeIndex MergeTypes(TypeIndex existing, IReadOnlyList<Type> added)
        {
            var mergedTypes = new Type[existing.Count + added.Count];
            var mergedSearchInfos = new TypeSearchInfo[mergedTypes.Length];
            var addedSearchInfos = new TypeSearchInfo[added.Count];
            for (var index = 0; index < added.Count; index++)
                addedSearchInfos[index] = CreateTypeSearchInfo(added[index]);

            var existingIndex = 0;
            var addedIndex = 0;
            var mergedIndex = 0;
            while (existingIndex < existing.Count && addedIndex < added.Count)
            {
                if (CompareTypeSearchInfo(
                        existing.SearchInfos[existingIndex],
                        addedSearchInfos[addedIndex]
                    ) <= 0)
                    Add(existing[existingIndex], existing.SearchInfos[existingIndex++]);
                else
                    Add(added[addedIndex], addedSearchInfos[addedIndex++]);
            }

            while (existingIndex < existing.Count)
                Add(existing[existingIndex], existing.SearchInfos[existingIndex++]);
            while (addedIndex < added.Count)
                Add(added[addedIndex], addedSearchInfos[addedIndex++]);
            return new(mergedTypes, mergedSearchInfos);

            void Add(Type type, TypeSearchInfo searchInfo)
            {
                mergedTypes[mergedIndex] = type;
                mergedSearchInfos[mergedIndex++] = searchInfo;
            }
        }

        internal static int CompareTypes(Type? left, Type? right)
        {
            var leftInfo = left == null ? null : GetTypeSearchInfo(left);
            var rightInfo = right == null ? null : GetTypeSearchInfo(right);
            return CompareTypeSearchInfo(leftInfo, rightInfo);
        }

        static int CompareTypeSearchInfo(TypeSearchInfo? left, TypeSearchInfo? right)
        {
            var assembly = string.Compare(
                left?.AssemblyName,
                right?.AssemblyName,
                StringComparison.Ordinal
            );
            if (assembly != 0)
                return assembly;

            return string.Compare(
                left?.FullName,
                right?.FullName,
                StringComparison.Ordinal
            );
        }

        static string GetAssemblyName(Assembly? assembly)
            => assembly == null
                ? string.Empty
                : assemblyNameCache.GetOrAdd(assembly, static value => value.GetName().Name ?? string.Empty);

        internal static Type? ResolveTypeName(string query)
        {
            var index = LoadIndex(out _);
            lock (IndexLock)
                if (GetExactTypeLookup().TryGetValue(query, out var exact))
                    return exact.Type;

            return ResolveUniqueType(index, query).Type;
        }

        internal static bool MatchesTypeName(Type type, string query)
            => MatchesTypeName(type, GetTypeSearchInfo(type), new(query));

        internal static bool MatchesTypeName(
            IReadOnlyList<Type> index,
            int position,
            TypeNameQuery query)
        {
            var type = index[position];
            return MatchesTypeName(type, GetTypeSearchInfo(index, position), query);
        }

        static bool MatchesTypeName(Type type, TypeSearchInfo info, TypeNameQuery query)
        {
            // FullName falls back to Name and always contains the unqualified runtime name.
            if (Contains(info.FullName, query.Text))
                return true;

            if ((query.HasGenericDisplay && info.IsGenericType || query.HasNestedDisplay && info.IsNested)
                && (Contains(DisplayTypeName(type, includeNamespace: false), query.Text)
                    || Contains(DisplayTypeName(type, includeNamespace: true), query.Text)))
                return true;

            return Contains(info.AssemblyName, query.Text)
                   || query.HasAssembly
                   && Contains($"{info.FullName}, {info.AssemblyName}", query.Text);
        }

        static TypeResolution ResolveUniqueType(IReadOnlyList<Type> index, string query)
        {
            var typeNameQuery = new TypeNameQuery(query);
            Type? match = null;
            for (var position = 0; position < index.Count; position++)
            {
                var type = index[position];
                if (!MatchesTypeName(index, position, typeNameQuery))
                    continue;

                if (match != null)
                    return default;

                match = type;
            }

            return new(match);
        }

        static TypeSearchInfo GetTypeSearchInfo(IReadOnlyList<Type> index, int position)
            => index is TypeIndex typeIndex
                ? typeIndex.SearchInfos[position]
                : GetTypeSearchInfo(index[position]);

        static Dictionary<string, TypeResolution> BuildExactTypeLookup(IReadOnlyList<Type> types)
        {
            var lookup = new Dictionary<string, TypeResolution>(
                types.Count * 2,
                StringComparer.OrdinalIgnoreCase
            );
            AddExactTypes(lookup, types);
            return lookup;
        }

        static Dictionary<string, TypeResolution> GetExactTypeLookup()
        {
            while (exactTypeLookup == null)
            {
                var currentTypes = cachedIndex!;
                var lookup = BuildExactTypeLookup(currentTypes);
                // metadata inspection can load another assembly reentrantly.
                if (!ReferenceEquals(currentTypes, cachedIndex))
                    continue;

                exactTypeLookup = lookup;
            }

            return exactTypeLookup;
        }

        static void AddExactTypes(Dictionary<string, TypeResolution> lookup, IReadOnlyList<Type> types)
        {
            for (var position = 0; position < types.Count; position++)
            {
                var type = types[position];
                var info = GetTypeSearchInfo(types, position);
                Add(info.Name);
                if (!string.Equals(info.FullName, info.Name, StringComparison.Ordinal))
                    Add(info.FullName);

                void Add(string name)
                {
                    if (lookup.TryAdd(name, new(type)))
                        return;

                    if (lookup[name].Type is { } existing && existing != type)
                        lookup[name] = default;
                }
            }
        }

        internal static string GetTypeName(Type type)
            => GetTypeSearchInfo(type).Name;

        internal static string GetFullTypeName(Type type)
            => GetTypeSearchInfo(type).FullName;

        static TypeSearchInfo GetTypeSearchInfo(Type type)
            => typeSearchInfoCache.GetOrAdd(type, static value => CreateTypeSearchInfo(value));

        static TypeSearchInfo CreateTypeSearchInfo(Type type)
        {
            var name = type.Name;
            return new(
                name,
                type.FullName ?? name,
                GetAssemblyName(type.Assembly),
                type.IsGenericType,
                type.IsNested,
                ClassifyType(type)
            );
        }

        static ReflectTypeKind ClassifyType(Type type)
        {
            if (type.IsInterface)
                return ReflectTypeKind.Interface;

            var baseType = type.BaseType;
            if (baseType == typeof(Enum))
                return ReflectTypeKind.Enum;
            if (baseType == typeof(MulticastDelegate))
                return ReflectTypeKind.Delegate;
            if (baseType == typeof(ValueType)
                && type != typeof(Enum))
                return ReflectTypeKind.Struct;
            return type == typeof(Delegate) || type == typeof(MulticastDelegate)
                ? ReflectTypeKind.Any
                : ReflectTypeKind.Class;
        }

        static string TypeCandidates(
            string header,
            IReadOnlyList<Type> candidates,
            int candidateCount)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
            {
                var type = candidates[index];
                builder.AppendLine($"- {FormatType(type, includeNamespace: true)}, {GetAssemblyName(type.Assembly)}");
            }

            AppendTruncation(builder, candidateCount, MaxCandidates, "candidates");
            return Trimmed(builder);
        }

        static string InvalidModeDiagnostic(string mode)
            => $"Unsupported reflect mode '{mode}'. Valid modes: {ValidModes}.";

        static string NormalizeQuery(string value)
            => value?.Trim() ?? string.Empty;

        static string Trimmed(StringBuilder builder)
        {
            while (builder.Length > 0 && char.IsWhiteSpace(builder[builder.Length - 1]))
                builder.Length--;

            return builder.ToString();
        }

        static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        internal static string GetShortTypeName(Type type)
        {
            var info = GetTypeSearchInfo(type);
            if (!info.IsGenericType && !info.IsNested)
                return info.Name;

            return info.ShortDisplayName ??= DisplayTypeName(type, includeNamespace: false);
        }

        static string ShortTypeName(Type type) => GetShortTypeName(type);

        static string Plural(int count)
            => count == 1 ? string.Empty : "s";

        static BridgeCommandResult Success(string returnValue) =>
            new()
            {
                outcome = ToolOutcome.Success,
                return_value = returnValue,
            };

        static BridgeCommandResult Error(string diagnostic) =>
            new()
            {
                outcome = ToolOutcome.Exception,
                diagnostic = diagnostic,
            };

        static BridgeCommandResult Ambiguous(string diagnostic) =>
            new()
            {
                outcome = ToolOutcome.AmbiguousTarget,
                diagnostic = diagnostic,
            };
    }

    readonly struct ReflectMode
    {
        public readonly ReflectCategory Category;
        public readonly ReflectTypeKind TypeKind;
        public readonly ReflectMemberKind MemberKind;

        public ReflectMode(ReflectCategory category, ReflectTypeKind typeKind, ReflectMemberKind memberKind)
        {
            Category = category;
            TypeKind = typeKind;
            MemberKind = memberKind;
        }
    }

    enum ReflectCategory : byte
    {
        Types,
        Members,
    }

    enum ReflectTypeKind : byte
    {
        Any,
        Class,
        Struct,
        Enum,
        Interface,
        Delegate,
    }

    enum ReflectMemberKind : byte
    {
        None,
        Field,
        Property,
        Method,
        Constructor,
    }

    enum TypeMatchKind : byte
    {
        None,
        Matched,
        Ambiguous,
    }

    readonly struct TypeNameQuery
    {
        public readonly string Text;
        public readonly bool HasGenericDisplay;
        public readonly bool HasNestedDisplay;
        public readonly bool HasAssembly;

        public TypeNameQuery(string text)
        {
            Text = text;
            HasGenericDisplay = text.IndexOf('<') >= 0;
            HasNestedDisplay = text.IndexOf('.') >= 0;
            HasAssembly = text.IndexOf(',') >= 0;
        }
    }

    readonly struct TypeResolution
    {
        public readonly Type? Type;

        public TypeResolution(Type? type) => Type = type;
    }

    readonly struct TypeSortEntry
    {
        public readonly Type Type;
        public readonly TypeSearchInfo SearchInfo;

        public TypeSortEntry(Type type, TypeSearchInfo searchInfo)
        {
            Type = type;
            SearchInfo = searchInfo;
        }
    }

    sealed class TypeIndex : IReadOnlyList<Type>
    {
        readonly Type[] types;
        internal readonly TypeSearchInfo[] SearchInfos;

        internal TypeIndex(Type[] types, TypeSearchInfo[] searchInfos)
        {
            this.types = types;
            SearchInfos = searchInfos;
        }

        public int Count => types.Length;
        public Type this[int index] => types[index];
        public IEnumerator<Type> GetEnumerator() => ((IEnumerable<Type>)types).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => types.GetEnumerator();
    }

    sealed class TypeSearchInfo
    {
        public readonly string Name;
        public readonly string FullName;
        public readonly string AssemblyName;
        public readonly bool IsGenericType;
        public readonly bool IsNested;
        public readonly ReflectTypeKind Kind;
        public string? ShortDisplayName;

        public TypeSearchInfo(
            string name,
            string fullName,
            string assemblyName,
            bool isGenericType,
            bool isNested,
            ReflectTypeKind kind)
        {
            Name = name;
            FullName = fullName;
            AssemblyName = assemblyName;
            IsGenericType = isGenericType;
            IsNested = isNested;
            Kind = kind;
        }
    }

    readonly struct TypeMatch
    {
        public readonly TypeMatchKind Kind;
        public readonly Type? Type;
        public readonly IReadOnlyList<Type> Candidates;
        public readonly int CandidateCount;

        TypeMatch(
            TypeMatchKind kind,
            Type? type,
            IReadOnlyList<Type> candidates,
            int candidateCount)
        {
            Kind = kind;
            Type = type;
            Candidates = candidates;
            CandidateCount = candidateCount;
        }

        public static TypeMatch None()
            => new(TypeMatchKind.None, null, Array.Empty<Type>(), 0);

        public static TypeMatch Matched(Type type)
            => new(TypeMatchKind.Matched, type, Array.Empty<Type>(), 1);

        public static TypeMatch Ambiguous(IReadOnlyList<Type> candidates, int candidateCount)
            => new(TypeMatchKind.Ambiguous, null, candidates, candidateCount);
    }

    readonly struct MemberDisplay
    {
        public readonly Type DeclaringType;
        public readonly ReflectMemberKind Kind;
        public readonly string Name;
        public readonly string Signature;
        public readonly int MatchRank;

        public MemberDisplay(Type declaringType, ReflectMemberKind kind, string name, string signature, int matchRank)
        {
            DeclaringType = declaringType;
            Kind = kind;
            Name = name;
            Signature = signature;
            MatchRank = matchRank;
        }
    }

    readonly struct MemberMatch
    {
        public readonly Type DeclaringType;
        public readonly ReflectMemberKind Kind;
        public readonly string Name;
        public readonly MemberInfo Member;
        public readonly int MatchRank;

        public MemberMatch(
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

    readonly struct WideMemberIndexEntry
    {
        public readonly Type DeclaringType;
        public readonly string Name;
        public readonly MemberInfo Member;

        public WideMemberIndexEntry(
            Type declaringType,
            MemberInfo member)
        {
            DeclaringType = declaringType;
            Name = member.Name;
            Member = member;
        }

        internal static int GetMetadataToken(MemberInfo member)
        {
            try
            {
                return member.MetadataToken;
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                return 0;
            }
        }
    }

    readonly struct WideMemberIndexSegment
    {
        public readonly string AssemblyName;
        public readonly WideMemberIndexEntry[] Entries;

        public WideMemberIndexSegment(string assemblyName, WideMemberIndexEntry[] entries)
        {
            AssemblyName = assemblyName;
            Entries = entries;
        }
    }

    sealed class WideMemberWithinTypeComparer : IComparer<WideMemberIndexEntry>
    {
        public static readonly WideMemberWithinTypeComparer Instance = new();

        WideMemberWithinTypeComparer() { }

        public int Compare(WideMemberIndexEntry left, WideMemberIndexEntry right)
        {
            var name = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return name != 0
                ? name
                : WideMemberIndexEntry.GetMetadataToken(left.Member)
                    .CompareTo(WideMemberIndexEntry.GetMetadataToken(right.Member));
        }
    }

    sealed class WideMemberIndex
    {
        internal volatile WideMemberIndexSegment[] Segments;

        public WideMemberIndex(WideMemberIndexSegment[] initial) => Segments = initial;

        internal void AddSegments(WideMemberIndexSegment[] added)
        {
            var current = Segments;
            var merged = new List<WideMemberIndexSegment>(current.Length + added.Length);
            var currentIndex = 0;
            var addedIndex = 0;
            while (currentIndex < current.Length && addedIndex < added.Length)
            {
                var comparison = string.Compare(
                    current[currentIndex].AssemblyName,
                    added[addedIndex].AssemblyName,
                    StringComparison.Ordinal
                );
                if (comparison < 0)
                    merged.Add(current[currentIndex++]);
                else if (comparison > 0)
                    merged.Add(added[addedIndex++]);
                else
                    merged.Add(Merge(current[currentIndex++], added[addedIndex++]));
            }

            while (currentIndex < current.Length)
                merged.Add(current[currentIndex++]);
            while (addedIndex < added.Length)
                merged.Add(added[addedIndex++]);
            Segments = merged.ToArray();

            static WideMemberIndexSegment Merge(
                WideMemberIndexSegment left,
                WideMemberIndexSegment right)
            {
                var entries = new WideMemberIndexEntry[left.Entries.Length + right.Entries.Length];
                var leftIndex = 0;
                var rightIndex = 0;
                var destinationIndex = 0;
                while (leftIndex < left.Entries.Length && rightIndex < right.Entries.Length)
                    entries[destinationIndex++] = reflect.CompareWideMemberEntries(
                        left.Entries[leftIndex],
                        right.Entries[rightIndex]
                    ) <= 0
                        ? left.Entries[leftIndex++]
                        : right.Entries[rightIndex++];

                while (leftIndex < left.Entries.Length)
                    entries[destinationIndex++] = left.Entries[leftIndex++];
                while (rightIndex < right.Entries.Length)
                    entries[destinationIndex++] = right.Entries[rightIndex++];
                return new(left.AssemblyName, entries);
            }
        }
    }
}
