#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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

        static reflect()
            => AppDomain.CurrentDomain.AssemblyLoad += static (_, _) => InvalidateIndex();

        static void InvalidateIndex()
        {
            lock (IndexLock)
            {
                cachedIndex = null;
                cachedLoadWarning = string.Empty;
            }
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
            var builder = new StringBuilder();
            AppendLoadWarning(builder, loadWarning);
            if (matches.Count == 0)
            {
                builder.Append("No types matched.");
                return Success(Trimmed(builder));
            }

            if (matches.Count > MaxTypeRows)
                AppendHeader(builder, "Types", matches.Count, MaxTypeRows);

            for (var matchIndex = 0; matchIndex < matches.Count && matchIndex < MaxTypeRows; matchIndex++)
                AppendType(builder, matches[matchIndex]);

            AppendTruncation(builder, matches.Count, MaxTypeRows, "types");
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
                return Ambiguous(TypeCandidates($"Multiple types match '{typeQuery}'. Rerun with a full type name or 'Full.Type.Name, AssemblyName'.", match.Candidates));

            var target = match.Type!;
            var normalizedMember = NormalizeQuery(memberQuery);
            var builder = new StringBuilder();
            AppendLoadWarning(builder, loadWarning);
            builder.AppendLine($"Members for {FormatType(target, includeNamespace: true)}");
            builder.AppendLine($"Assembly: {target.Assembly.GetName().Name}");
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
            var matches = new List<MemberDisplay>();
            foreach (var type in index)
                matches.AddRange(GetDisplayMembers(type, mode.MemberKind, memberQuery));

            matches.Sort(CompareMembers);

            var builder = new StringBuilder();
            AppendLoadWarning(builder, loadWarning);
            if (matches.Count == 0)
            {
                builder.Append("No members matched.");
                return Success(Trimmed(builder));
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
                foreach (var method in GetMethods(type, memberQuery))
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

        static MethodInfo[] GetMethods(Type type, string memberQuery = "")
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            var includeAccessors = IsAccessorQuery(memberQuery);
            var filtered = new List<MethodInfo>(methods.Length);
            foreach (var method in methods)
                if (includeAccessors || !IsPropertyOrEventAccessor(method))
                    filtered.Add(method);

            return filtered.ToArray();
        }

        static bool IsAccessorQuery(string memberQuery)
            => memberQuery.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("set_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("add_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("remove_", StringComparison.OrdinalIgnoreCase)
               || memberQuery.StartsWith("raise_", StringComparison.OrdinalIgnoreCase);

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
                foreach (var method in GetMethods(type, memberQuery))
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

        internal static IReadOnlyList<Type> LoadIndexForHelpers()
            => LoadIndex(out _);

        static IReadOnlyList<Type> BuildIndex(out string warning)
        {
            var types = new List<Type>();
            var warnings = new StringBuilder();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
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

                    AppendLoadFailure(assembly, exception);
                }
                catch (Exception exception)
                {
                    AppendLoadFailure(assembly, exception);
                }
            }

            SortTypes(types);
            warning = Trimmed(warnings);
            return types;

            void AppendLoadFailure(Assembly assembly, Exception exception)
            {
                if (warnings.Length == 0)
                    warnings.AppendLine("Warning: some loaded assemblies could not be fully reflected.");

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
            var builder = new StringBuilder();
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
            var builder = new StringBuilder();
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
            var builder = new StringBuilder();
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
            var builder = new StringBuilder();
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
            var hierarchy = new Stack<Type>();
            for (var current = type; current != null; current = current.DeclaringType)
                hierarchy.Push(current);

            var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
            var argumentIndex = 0;
            var builder = new StringBuilder();
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
            var builder = new StringBuilder();
            builder.AppendLine(header);
            builder.AppendLine("Candidates:");
            for (var index = 0; index < candidates.Count && index < MaxCandidates; index++)
            {
                var type = candidates[index];
                builder.AppendLine($"- {FormatType(type, includeNamespace: true)}, {type.Assembly.GetName().Name}");
            }

            AppendTruncation(builder, candidates.Count, MaxCandidates, "candidates");
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
