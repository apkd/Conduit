#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Conduit
{
    static class reflect
    {
        const int MaxTypeRows = 100;
        const int MaxWideMemberRows = 200;
        const int MaxCandidates = 25;
        const string ValidModes = "types, classes, structs, enums, interfaces, delegates, members, fields, properties, methods, constructors";
        static readonly object IndexLock = new();
        static IReadOnlyList<Type>? cachedIndex;
        static string cachedLoadWarning = string.Empty;

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

            var matches = new List<Type>();
            foreach (var type in index)
            {
                if (!MatchesTypeKind(type, mode.TypeKind))
                    continue;

                if (normalizedType.Length > 0 && !MatchesType(type, normalizedType))
                    continue;

                if (normalizedMember.Length > 0 && !TypeDeclaresMatchingMember(type, mode.MemberKind, normalizedMember))
                    continue;

                matches.Add(type);
            }

            SortTypes(matches);
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            AppendLoadWarning(builder, loadWarning);
            if (matches.Count == 0)
            {
                builder.Append("No types matched.");
                return Success(builder.TrimEnd().ToString());
            }

            if (matches.Count > MaxTypeRows)
                AppendHeader(builder, "Types", matches.Count, MaxTypeRows);

            for (var matchIndex = 0; matchIndex < matches.Count && matchIndex < MaxTypeRows; matchIndex++)
                AppendType(builder, matches[matchIndex]);

            AppendTruncation(builder, matches.Count, MaxTypeRows, "types");
            return Success(builder.TrimEnd().ToString());
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
                return Ambiguous(TypeCandidates($"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.", match.Candidates));

            var target = match.Type!;
            var normalizedMember = NormalizeQuery(memberQuery);
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            AppendLoadWarning(builder, loadWarning);
            builder.AppendLine($"Members for {FormatType(target, includeNamespace: true)}");
            builder.AppendLine($"Assembly: {target.Assembly.GetName().Name}");
            AppendTypeHierarchy(builder, target);
            builder.AppendLine();

            var appendedAny = AppendTypeScopedMembers(builder, target, mode.MemberKind, normalizedMember);

            if (!appendedAny)
                builder.Append("No members matched.");

            return Success(builder.TrimEnd().ToString());
        }

        static BridgeCommandResult SearchWideMembers(
            IReadOnlyList<Type> index,
            ReflectMode mode,
            string memberQuery,
            string loadWarning
        )
        {
            var matches = new List<MemberDisplay>();
            foreach (var type in index)
                matches.AddRange(GetDisplayMembers(type, mode.MemberKind, memberQuery));

            matches.Sort(CompareMembers);

            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            AppendLoadWarning(builder, loadWarning);
            if (matches.Count == 0)
            {
                builder.Append("No members matched.");
                return Success(builder.TrimEnd().ToString());
            }

            if (matches.Count > MaxWideMemberRows)
                AppendHeader(builder, "Members", matches.Count, MaxWideMemberRows);

            Type? currentType = null;
            ReflectMemberKind currentKind = ReflectMemberKind.None;
            var shown = 0;
            foreach (var member in matches)
            {
                if (shown == MaxWideMemberRows)
                    break;

                if (currentType != member.DeclaringType)
                {
                    if (shown > 0)
                        builder.AppendLine();

                    currentType = member.DeclaringType;
                    currentKind = ReflectMemberKind.None;
                    builder.AppendLine($"Containing Type: {FormatType(currentType, includeNamespace: true)} ({currentType.Assembly.GetName().Name})");
                }

                if (currentKind != member.Kind)
                {
                    currentKind = member.Kind;
                    builder.AppendLine($"  {MemberKindHeader(currentKind)}:");
                }

                builder.AppendLine($"  - {member.Signature}");
                shown++;
            }

            AppendTruncation(builder, matches.Count, MaxWideMemberRows, "members");
            return Success(builder.TrimEnd().ToString());
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

                builder.AppendLine($"{label} ({containingType.Assembly.GetName().Name})");
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
            builder.Append(type.Assembly.GetName().Name);
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
            builder.Append(GetFields(type).Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(", properties=");
            builder.Append(GetProperties(type).Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(", methods=");
            builder.Append(GetMethods(type).Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(", constructors=");
            builder.Append(GetConstructors(type).Length.ToString(CultureInfo.InvariantCulture));
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
                    builder.AppendLine($"  {MemberKindHeader(currentKind)}:");
                }

                builder.AppendLine($"  - {member.Signature}");
                appended++;
            }
        }

        static void AppendTruncation(StringBuilder builder, int count, int maxRows, string label)
        {
            if (count <= maxRows)
                return;

            builder.AppendLine();
            builder.Append("Truncated: ");
            builder.Append((count - maxRows).ToString(CultureInfo.InvariantCulture));
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
                        members.Add(new(type, ReflectMemberKind.Field, field.Name, FormatField(field), rank));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Property)
                foreach (var property in GetProperties(type))
                    if (TryGetMemberMatchRank(property, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Property, property.Name, FormatProperty(property), rank));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Method)
                foreach (var method in GetMethods(type))
                    if (TryGetMemberMatchRank(method, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Method, method.Name, FormatMethod(method), rank));

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                foreach (var constructor in GetConstructors(type))
                    if (TryGetMemberMatchRank(constructor, memberQuery, out var rank))
                        members.Add(new(type, ReflectMemberKind.Constructor, constructor.Name, FormatConstructor(constructor), rank));

            return members;
        }

        static FieldInfo[] GetFields(Type type)
            => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        static PropertyInfo[] GetProperties(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        static MethodInfo[] GetMethods(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var filtered = new List<MethodInfo>(methods.Length);
            foreach (var method in methods)
                if (!IsPropertyOrEventAccessor(method))
                    filtered.Add(method);

            return filtered.ToArray();
        }

        static ConstructorInfo[] GetConstructors(Type type)
            => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

        static bool TypeDeclaresMatchingMember(Type type, ReflectMemberKind kind, string memberQuery)
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
                foreach (var method in GetMethods(type))
                    if (MatchesMember(method, memberQuery))
                        return true;

            if (kind is ReflectMemberKind.None or ReflectMemberKind.Constructor)
                foreach (var constructor in GetConstructors(type))
                    if (MatchesMember(constructor, memberQuery))
                        return true;

            return false;
        }

        static bool MatchesMember(MemberInfo member, string query)
            => MemberMatchRank(member, query) < int.MaxValue;

        static bool TryGetMemberMatchRank(MemberInfo member, string query, out int rank)
        {
            rank = MemberMatchRank(member, query);
            return rank < int.MaxValue;
        }

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

            if (member is ConstructorInfo constructor)
            {
                var declaringType = constructor.DeclaringType;
                if (string.Equals(query, "ctor", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(query, ".ctor", StringComparison.OrdinalIgnoreCase))
                    return 0;

                if (declaringType != null)
                {
                    var shortName = ShortTypeName(declaringType);
                    if (string.Equals(shortName, query, StringComparison.OrdinalIgnoreCase))
                        return 0;

                    if (shortName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                        return 1;

                    if (Contains(shortName, query))
                        return 2;
                }
            }

            return int.MaxValue;
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

        static IReadOnlyList<Type> LoadIndex(out string warning)
        {
            lock (IndexLock)
            {
                if (cachedIndex != null)
                {
                    warning = cachedLoadWarning;
                    return cachedIndex;
                }

                cachedIndex = BuildIndex(out cachedLoadWarning);
                warning = cachedLoadWarning;
                return cachedIndex;
            }
        }

        static IReadOnlyList<Type> BuildIndex(out string warning)
        {
            // Unity's native index avoids Assembly.GetTypes(), which can stall the editor for minutes
            // in projects with many packages. Interfaces are recovered from the indexed concrete types.
            var uniqueTypes = new HashSet<Type>(UnityEditor.TypeCache.GetTypesDerivedFrom<object>());
            var indexedTypes = new List<Type>(uniqueTypes);
            foreach (var type in indexedTypes)
                foreach (var interfaceType in type.GetInterfaces())
                    uniqueTypes.Add(interfaceType);

            var types = new List<Type>(uniqueTypes);
            SortTypes(types);
            warning = string.Empty;
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
                ReflectTypeKind.Any    => true,
                ReflectTypeKind.Class  => type.IsClass && !typeof(Delegate).IsAssignableFrom(type),
                ReflectTypeKind.Struct => type.IsValueType && !type.IsEnum,
                ReflectTypeKind.Enum   => type.IsEnum,
                ReflectTypeKind.Interface => type.IsInterface,
                ReflectTypeKind.Delegate  => type.IsSubclassOf(typeof(MulticastDelegate)),
                _                      => true,
            };

        static string FormatField(FieldInfo field)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            AppendFieldAccess(builder, field);
            AppendFieldModifiers(builder, field);
            builder.Append(FormatType(field.FieldType));
            builder.Append(' ');
            builder.Append(field.Name);
            return builder.ToString();
        }

        static string FormatProperty(PropertyInfo property)
        {
            var accessor = property.GetMethod ?? property.SetMethod;
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            if (accessor != null)
            {
                builder.Append(Access(accessor));
                builder.Append(' ');
                if (accessor.IsStatic)
                    builder.Append("static ");
            }

            builder.Append(FormatType(property.PropertyType));
            builder.Append(' ');
            builder.Append(FormatPropertyName(property));
            builder.Append(" { ");
            AppendPropertyAccessor(builder, "get", property.GetMethod, accessor);
            if (property.GetMethod != null && property.SetMethod != null)
                builder.Append(' ');
            AppendPropertyAccessor(builder, "set", property.SetMethod, accessor);
            builder.Append(" }");
            return builder.ToString();
        }

        static string FormatPropertyName(PropertyInfo property)
        {
            var parameters = property.GetIndexParameters();
            if (parameters.Length == 0)
                return property.Name;

            return "this[" + FormatParameters(parameters) + "]";
        }

        static void AppendPropertyAccessor(StringBuilder builder, string name, MethodInfo? accessor, MethodInfo? primaryAccessor)
        {
            if (accessor == null)
                return;

            if (primaryAccessor != null && Access(accessor) != Access(primaryAccessor))
            {
                builder.Append(Access(accessor));
                builder.Append(' ');
            }

            builder.Append(name);
            builder.Append(';');
        }

        static string FormatMethod(MethodInfo method)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.Append(Access(method));
            builder.Append(' ');
            AppendMethodModifiers(builder, method);
            builder.Append(FormatType(method.ReturnType));
            builder.Append(' ');
            builder.Append(method.Name);
            AppendGenericArguments(builder, method.GetGenericArguments());
            builder.Append('(');
            builder.Append(FormatParameters(method.GetParameters()));
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatConstructor(ConstructorInfo constructor)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            if (constructor.IsStatic)
            {
                builder.Append("static ");
            }
            else
            {
                builder.Append(Access(constructor));
                builder.Append(' ');
            }

            builder.Append(constructor.DeclaringType == null ? ".ctor" : DisplayTypeName(constructor.DeclaringType, includeNamespace: false));
            builder.Append('(');
            builder.Append(FormatParameters(constructor.GetParameters()));
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatParameters(ParameterInfo[] parameters)
        {
            var builder = new StringBuilder();
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
                if (parameter.IsOut)
                    builder.Append("out ");
                else if (parameter.GetCustomAttribute<InAttribute>() != null)
                    builder.Append("in ");
                else
                    builder.Append("ref ");

                parameterType = parameterType.GetElementType() ?? parameterType;
            }

            builder.Append(FormatType(parameterType));
            builder.Append(' ');
            builder.Append(parameter.Name);
            if (parameter.HasDefaultValue)
            {
                builder.Append(" = ");
                builder.Append(FormatDefaultValue(parameter.DefaultValue));
            }
        }

        static string FormatDefaultValue(object? value)
            => value switch
            {
                null        => "null",
                string text => "\"" + text.Replace("\"", "\\\"") + "\"",
                char c      => "'" + c + "'",
                bool b      => b ? "true" : "false",
                Enum e      => e.GetType().Name + "." + e,
                _           => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
            };

        static string FormatType(Type type, bool includeNamespace = false)
        {
            if (type.IsByRef)
                type = type.GetElementType() ?? type;

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
            if (!type.IsGenericType)
                return PlainTypeName(type, includeNamespace);

            var definitionName = PlainTypeName(type, includeNamespace);
            var tick = definitionName.IndexOf('`');
            if (tick >= 0)
                definitionName = definitionName[..tick];

            var arguments = type.GetGenericArguments();
            var builder = new StringBuilder(definitionName);
            builder.Append('<');
            for (var index = 0; index < arguments.Length; index++)
            {
                if (index > 0)
                    builder.Append(", ");

                builder.Append(FormatType(arguments[index], includeNamespace));
            }

            builder.Append('>');
            return builder.ToString();
        }

        static string PlainTypeName(Type type, bool includeNamespace)
        {
            var name = includeNamespace
                ? type.FullName ?? type.Name
                : type.Name;

            name = name.Replace('+', '.');
            if (!includeNamespace || string.IsNullOrEmpty(type.Namespace))
                return name;

            return name;
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
            builder.Append(Access(field));
            builder.Append(' ');
        }

        static void AppendFieldModifiers(StringBuilder builder, FieldInfo field)
        {
            if (field.IsStatic)
                if (!field.IsLiteral)
                    builder.Append("static ");
            if (field.IsLiteral)
                builder.Append("const ");
            else if (field.IsInitOnly)
                builder.Append("readonly ");
        }

        static void AppendMethodModifiers(StringBuilder builder, MethodInfo method)
        {
            if (method.IsStatic)
                builder.Append("static ");
            if (method.IsAbstract)
                builder.Append("abstract ");
            else if (method.IsVirtual && !method.IsFinal)
                builder.Append(IsOverride(method) ? "override " : "virtual ");
        }

        static bool IsOverride(MethodInfo method)
        {
            try
            {
                return method.GetBaseDefinition() != method;
            }
            catch (InvalidOperationException)
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

                builder.Append(genericArguments[index].Name);
            }

            builder.Append('>');
        }

        static string Access(MethodBase method)
        {
            if (method.IsPublic)
                return "public";
            if (method.IsFamily)
                return "protected";
            if (method.IsFamilyOrAssembly)
                return "protected internal";
            if (method.IsFamilyAndAssembly)
                return "private protected";
            if (method.IsAssembly)
                return "internal";
            return "private";
        }

        static string Access(FieldInfo field)
        {
            if (field.IsPublic)
                return "public";
            if (field.IsFamily)
                return "protected";
            if (field.IsFamilyOrAssembly)
                return "protected internal";
            if (field.IsFamilyAndAssembly)
                return "private protected";
            if (field.IsAssembly)
                return "internal";
            return "private";
        }

        static bool IsPropertyOrEventAccessor(MethodInfo method)
            => method.IsSpecialName
               && (method.Name.StartsWith("get_", StringComparison.Ordinal)
                   || method.Name.StartsWith("set_", StringComparison.Ordinal)
                   || method.Name.StartsWith("add_", StringComparison.Ordinal)
                   || method.Name.StartsWith("remove_", StringComparison.Ordinal)
                   || method.Name.StartsWith("raise_", StringComparison.Ordinal));

        static string JoinTypes(Type[] types, int max)
        {
            var builder = new StringBuilder();
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
                builder.Append((types.Length - shown).ToString(CultureInfo.InvariantCulture));
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
                return "struct";
            return "class";
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

        static void SortTypes(List<Type> types) => types.Sort(CompareTypes);

        static int CompareTypes(Type? left, Type? right)
        {
            var assembly = string.Compare(left?.Assembly.GetName().Name, right?.Assembly.GetName().Name, StringComparison.Ordinal);
            if (assembly != 0)
                return assembly;

            return string.Compare(left?.FullName ?? left?.Name, right?.FullName ?? right?.Name, StringComparison.Ordinal);
        }

        static string TypeCandidates(string header, IReadOnlyList<Type> candidates)
        {
            using var pooledBuilder = ConduitUtility.GetStringBuilder(out var builder);
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
            {
                var type = candidates[index];
                builder.AppendLine($"- {FormatType(type, includeNamespace: true)}, {type.Assembly.GetName().Name}");
            }

            AppendTruncation(builder, candidates.Count, MaxCandidates, "candidates");
            return builder.TrimEnd().ToString();
        }

        static string InvalidModeDiagnostic(string mode)
            => $"Unsupported reflect mode '{mode}'. Valid modes: {ValidModes}.";

        static string NormalizeQuery(string value)
            => value?.Trim() ?? string.Empty;

        static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static string ShortTypeName(Type type)
            => DisplayTypeName(type, includeNamespace: false);

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

    readonly struct TypeMatch
    {
        public readonly TypeMatchKind Kind;
        public readonly Type? Type;
        public readonly IReadOnlyList<Type> Candidates;

        TypeMatch(TypeMatchKind kind, Type? type, IReadOnlyList<Type> candidates)
        {
            Kind = kind;
            Type = type;
            Candidates = candidates;
        }

        public static TypeMatch None()
            => new(TypeMatchKind.None, null, Array.Empty<Type>());

        public static TypeMatch Matched(Type type)
            => new(TypeMatchKind.Matched, type, Array.Empty<Type>());

        public static TypeMatch Ambiguous(IReadOnlyList<Type> candidates)
            => new(TypeMatchKind.Ambiguous, null, candidates);
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
}
