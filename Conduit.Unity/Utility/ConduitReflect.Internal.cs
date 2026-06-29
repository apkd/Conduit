#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace Conduit
{
    public static partial class ConduitReflect
    {
        const int MaxCandidates = 25;
        const string ValidModes = "types, classes, structs, enums, interfaces, delegates, members, fields, properties, methods, constructors";
        static readonly object indexLock = new();
        static IReadOnlyList<Type>? cachedIndex;

        static T[] FindManyCore<T>(string mode, string? type, string? member) where T : class
        {
            if (!TryParseMode(mode, out var queryMode))
                throw new InvalidOperationException(InvalidModeDiagnostic(mode));

            ValidateResultType<T>(queryMode);
            var index = LoadIndex();
            return queryMode.Category == ReflectCategory.Types
                ? FindTypes<T>(index, queryMode, type, member)
                : FindMembers<T>(index, queryMode, type, member);
        }

        static T FindOneCore<T>(string mode, string? type, string? member) where T : class
        {
            if (!TryParseMode(mode, out var queryMode))
                throw new InvalidOperationException(InvalidModeDiagnostic(mode));

            ValidateResultType<T>(queryMode);
            var index = LoadIndex();
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
            using var pooledCandidates = ConduitUtility.GetPooledList<Type>(out var candidates);
            foreach (var type in index)
                if (MatchesTypeKind(type, mode.TypeKind))
                    candidates.Add(type);

            // singular type lookup keeps the report tool's exact-name precedence before substring matches.
            var match = MatchSingleType(candidates, typeQuery);
            if (match.Kind == TypeMatchKind.Ambiguous)
                throw new InvalidOperationException(TypeCandidates($"Multiple reflected results match {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.", match.Candidates));

            if (match.Kind == TypeMatchKind.None
                || (normalizedMember.Length > 0 && !TypeDeclaresMatchingMember(match.Type!, ReflectMemberKind.None, normalizedMember)))
                throw new InvalidOperationException($"No reflected result matched {FormatQuery(FormatMode(mode), typeQuery, memberQuery)}.");

            return (T)(object)match.Type!;
        }

        static T[] FindTypes<T>(IReadOnlyList<Type> index, ReflectMode mode, string? typeQuery, string? memberQuery) where T : class
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = NormalizeQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Length == 0)
                throw new InvalidOperationException("reflect type modes require `type` or `member`.");

            using var pooledMatches = ConduitUtility.GetPooledList<Type>(out var matches);
            foreach (var type in index)
            {
                if (!MatchesTypeKind(type, mode.TypeKind))
                    continue;

                if (normalizedType.Length > 0 && !MatchesType(type, normalizedType))
                    continue;

                if (normalizedMember.Length > 0 && !TypeDeclaresMatchingMember(type, ReflectMemberKind.None, normalizedMember))
                    continue;

                matches.Add(type);
            }

            SortTypes(matches);
            return CastResults<T, Type>(matches);
        }

        static T[] FindMembers<T>(IReadOnlyList<Type> index, ReflectMode mode, string? typeQuery, string? memberQuery) where T : class
        {
            var normalizedType = NormalizeQuery(typeQuery);
            var normalizedMember = NormalizeQuery(memberQuery);
            if (normalizedType.Length == 0 && normalizedMember.Length == 0)
                throw new InvalidOperationException("reflect member modes require `type` or `member`.");

            var effectiveKind = GetEffectiveMemberKind<T>(mode.MemberKind);
            using var pooledMatches = ConduitUtility.GetPooledList<MemberInfo>(out var matches);
            if (normalizedType.Length > 0)
                CollectTypeScopedMembers(index, normalizedType, normalizedMember, effectiveKind, matches);
            else
                CollectWideMembers(index, normalizedMember, effectiveKind, matches);

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
            var match = MatchSingleType(index, typeQuery);
            if (match.Kind == TypeMatchKind.None)
                throw new InvalidOperationException($"No type matched '{typeQuery}'.");

            if (match.Kind == TypeMatchKind.Ambiguous)
                throw new InvalidOperationException(TypeCandidates($"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.", match.Candidates));

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

        static void CollectWideMembers(
            IReadOnlyList<Type> index,
            string memberQuery,
            ReflectMemberKind kind,
            List<MemberInfo> matches
        )
        {
            // wide searches stay declared-only so the same inherited method is reported once per declaring type.
            foreach (var type in index)
                CollectDeclaredMembers(type, kind, memberQuery, matches);
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
                foreach (var method in GetMethods(type))
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

        static IReadOnlyList<Type> LoadIndex()
        {
            lock (indexLock)
            {
                if (cachedIndex != null)
                    return cachedIndex;

                cachedIndex = BuildIndex();
                return cachedIndex;
            }
        }

        static IReadOnlyList<Type> BuildIndex()
        {
            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                try
                {
                    types.AddRange(assembly.GetTypes());
                }
                catch (ReflectionTypeLoadException exception)
                {
                    // partially loaded assemblies still contain useful project and package types.
                    foreach (var type in exception.Types)
                        if (type != null)
                            types.Add(type);
                }
            }

            SortTypes(types);
            return types;
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

        static bool MatchesTypeKind(Type type, ReflectTypeKind kind)
            => kind switch
            {
                ReflectTypeKind.Any       => true,
                ReflectTypeKind.Class     => type.IsClass && !typeof(Delegate).IsAssignableFrom(type),
                ReflectTypeKind.Struct    => type.IsValueType && !type.IsEnum,
                ReflectTypeKind.Enum      => type.IsEnum,
                ReflectTypeKind.Interface => type.IsInterface,
                ReflectTypeKind.Delegate  => type.IsSubclassOf(typeof(MulticastDelegate)),
                _                         => true,
            };

        static bool TypeDeclaresMatchingMember(Type type, ReflectMemberKind kind, string memberQuery)
        {
            using var pooledMatches = ConduitUtility.GetPooledList<MemberInfo>(out var matches);
            CollectDeclaredMembers(type, kind, memberQuery, matches);
            return matches.Count > 0;
        }

        static FieldInfo[] GetFields(Type type)
            => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        static PropertyInfo[] GetProperties(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        static MethodInfo[] GetMethods(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            using var pooledFiltered = ConduitUtility.GetPooledList<MethodInfo>(out var filtered);
            foreach (var method in methods)
                if (!IsPropertyOrEventAccessor(method))
                    filtered.Add(method);

            return filtered.ToArray();
        }

        static ConstructorInfo[] GetConstructors(Type type)
            => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        static bool MatchesMember(MemberInfo member, string query)
            => MemberMatchRank(member, query) < int.MaxValue;

        static int MemberMatchRank(MemberInfo member, string query)
        {
            if (query.Length == 0)
                return 0;

            if (string.Equals(member.Name, query, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (member.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 1;

            if (Contains(member.Name, query))
                return 2;

            if (member is ConstructorInfo constructor && constructor.DeclaringType != null)
                return ConstructorMatchRank(constructor.DeclaringType, query);

            return int.MaxValue;
        }

        static int ConstructorMatchRank(Type declaringType, string query)
        {
            if (string.Equals(query, "ctor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(query, ".ctor", StringComparison.OrdinalIgnoreCase))
                return 0;

            var shortName = ShortTypeName(declaringType);
            if (string.Equals(shortName, query, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (shortName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 1;

            return Contains(shortName, query) ? 2 : int.MaxValue;
        }

        static bool MatchesType(Type type, string query)
            => Contains(type.FullName ?? type.Name, query)
               || Contains(type.Name, query)
               || Contains(DisplayTypeName(type, includeNamespace: false), query)
               || Contains(DisplayTypeName(type, includeNamespace: true), query)
               || Contains($"{type.FullName}, {type.Assembly.GetName().Name}", query);

        static TypeMatch MatchSingleType(IReadOnlyList<Type> index, string query)
        {
            var exactQualified = FindTypes(index, type => string.Equals($"{type.FullName}, {type.Assembly.GetName().Name}", query, StringComparison.OrdinalIgnoreCase));
            if (exactQualified.Count > 0)
                return SelectTypeMatch(exactQualified);

            var exactFullName = FindTypes(index, type => string.Equals(type.FullName, query, StringComparison.OrdinalIgnoreCase));
            if (exactFullName.Count > 0)
                return SelectTypeMatch(exactFullName);

            var exactDisplayName = FindTypes(index, type => string.Equals(DisplayTypeName(type, includeNamespace: true), query, StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(DisplayTypeName(type, includeNamespace: false), query, StringComparison.OrdinalIgnoreCase));
            if (exactDisplayName.Count > 0)
                return SelectTypeMatch(exactDisplayName);

            var contains = FindTypes(index, type => MatchesType(type, query));
            if (contains.Count == 0)
                return TypeMatch.None();

            return SelectTypeMatch(contains);
        }

        static TypeMatch SelectTypeMatch(List<Type> matches)
        {
            SortTypes(matches);
            return matches.Count == 1
                ? TypeMatch.Matched(matches[0])
                : TypeMatch.Ambiguous(matches);
        }

        static List<Type> FindTypes(IReadOnlyList<Type> index, Func<Type, bool> predicate)
        {
            var matches = new List<Type>();
            foreach (var type in index)
                if (predicate(type))
                    matches.Add(type);

            return matches;
        }

        static bool IsPropertyOrEventAccessor(MethodInfo method)
            => method.IsSpecialName
               && (method.Name.StartsWith("get_", StringComparison.Ordinal)
                   || method.Name.StartsWith("set_", StringComparison.Ordinal)
                   || method.Name.StartsWith("add_", StringComparison.Ordinal)
                   || method.Name.StartsWith("remove_", StringComparison.Ordinal)
                   || method.Name.StartsWith("raise_", StringComparison.Ordinal));

        static string DisplayTypeName(Type type, bool includeNamespace)
        {
            if (!type.IsGenericType)
                return PlainTypeName(type, includeNamespace);

            var definitionName = PlainTypeName(type, includeNamespace);
            var tick = definitionName.IndexOf('`');
            if (tick >= 0)
                definitionName = definitionName[..tick];

            var arguments = type.GetGenericArguments();
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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
                ? type.FullName ?? type.Name
                : type.Name;

            return name.Replace('+', '.');
        }

        static void SortTypes(List<Type> types) => types.Sort(CompareTypes);

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
        {
            var assembly = string.Compare(left?.Assembly.GetName().Name, right?.Assembly.GetName().Name, StringComparison.Ordinal);
            if (assembly != 0)
                return assembly;

            return string.Compare(left?.FullName ?? left?.Name, right?.FullName ?? right?.Name, StringComparison.Ordinal);
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

        static string FormatResultCandidates<T>(string header, IReadOnlyList<T> candidates) where T : class
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
                builder.AppendLine("- " + FormatCandidate(candidates[index]));

            AppendTruncation(builder, candidates.Count, MaxCandidates, "candidates");
            return builder.TrimEnd().ToString();
        }

        static string FormatCandidate(object candidate)
            => candidate switch
            {
                Type type         => $"{DisplayTypeName(type, includeNamespace: true)}, {type.Assembly.GetName().Name}",
                MemberInfo member => $"{DisplayTypeName(member.DeclaringType ?? typeof(object), includeNamespace: true)}.{member.Name} | {member.MemberType} | {member.Module.Assembly.GetName().Name}",
                _                 => candidate.ToString() ?? string.Empty,
            };

        static string TypeCandidates(string header, IReadOnlyList<Type> candidates)
            => FormatResultCandidates(header, candidates);

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
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
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

        static string ShortTypeName(Type type)
            => DisplayTypeName(type, includeNamespace: false);
    }
}
