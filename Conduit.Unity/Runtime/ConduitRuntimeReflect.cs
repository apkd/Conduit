#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Conduit.Runtime
{
    /// <summary>Provides reflection lookup helpers over assemblies loaded by the player.</summary>
    public static class ConduitRuntimeReflect
    {
        const BindingFlags AllMembers =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.FlattenHierarchy;

        /// <summary>Finds exactly one type.</summary>
        public static Type Type(string? type = null, string? member = null)
            => SelectSingle("type", FindTypes("types", type, member));

        /// <summary>Finds types.</summary>
        public static Type[] Types(string? type = null, string? member = null)
            => FindTypes("types", type, member);

        /// <summary>Finds exactly one class.</summary>
        public static Type Class(string? type = null, string? member = null)
            => SelectSingle("class", FindTypes("classes", type, member));

        /// <summary>Finds classes.</summary>
        public static Type[] Classes(string? type = null, string? member = null)
            => FindTypes("classes", type, member);

        /// <summary>Finds exactly one struct.</summary>
        public static Type Struct(string? type = null, string? member = null)
            => SelectSingle("struct", FindTypes("structs", type, member));

        /// <summary>Finds structs.</summary>
        public static Type[] Structs(string? type = null, string? member = null)
            => FindTypes("structs", type, member);

        /// <summary>Finds exactly one enum.</summary>
        public static Type Enum(string? type = null, string? member = null)
            => SelectSingle("enum", FindTypes("enums", type, member));

        /// <summary>Finds enums.</summary>
        public static Type[] Enums(string? type = null, string? member = null)
            => FindTypes("enums", type, member);

        /// <summary>Finds exactly one interface.</summary>
        public static Type Interface(string? type = null, string? member = null)
            => SelectSingle("interface", FindTypes("interfaces", type, member));

        /// <summary>Finds interfaces.</summary>
        public static Type[] Interfaces(string? type = null, string? member = null)
            => FindTypes("interfaces", type, member);

        /// <summary>Finds exactly one delegate.</summary>
        public static Type Delegate(string? type = null, string? member = null)
            => SelectSingle("delegate", FindTypes("delegates", type, member));

        /// <summary>Finds delegates.</summary>
        public static Type[] Delegates(string? type = null, string? member = null)
            => FindTypes("delegates", type, member);

        /// <summary>Finds exactly one method.</summary>
        public static MethodInfo Method(string? type = null, string? member = null)
            => SelectSingle("method", FindMembers<MethodInfo>("methods", type, member));

        /// <summary>Finds methods.</summary>
        public static MethodInfo[] Methods(string? type = null, string? member = null)
            => FindMembers<MethodInfo>("methods", type, member);

        /// <summary>Finds exactly one field.</summary>
        public static FieldInfo Field(string? type = null, string? member = null)
            => SelectSingle("field", FindMembers<FieldInfo>("fields", type, member));

        /// <summary>Finds fields.</summary>
        public static FieldInfo[] Fields(string? type = null, string? member = null)
            => FindMembers<FieldInfo>("fields", type, member);

        /// <summary>Finds exactly one property.</summary>
        public static PropertyInfo Property(string? type = null, string? member = null)
            => SelectSingle("property", FindMembers<PropertyInfo>("properties", type, member));

        /// <summary>Finds properties.</summary>
        public static PropertyInfo[] Properties(string? type = null, string? member = null)
            => FindMembers<PropertyInfo>("properties", type, member);

        /// <summary>Finds exactly one constructor.</summary>
        public static ConstructorInfo Constructor(string? type = null, string? member = null)
            => SelectSingle("constructor", FindMembers<ConstructorInfo>("constructors", type, member));

        /// <summary>Finds constructors.</summary>
        public static ConstructorInfo[] Constructors(string? type = null, string? member = null)
            => FindMembers<ConstructorInfo>("constructors", type, member);

        /// <summary>Finds exactly one member.</summary>
        public static MemberInfo Member(string? type = null, string? member = null)
            => SelectSingle("member", FindMembers<MemberInfo>("members", type, member));

        /// <summary>Finds members.</summary>
        public static MemberInfo[] Members(string? type = null, string? member = null)
            => FindMembers<MemberInfo>("members", type, member);

        internal static Type? ResolveType(string query)
        {
            var matches = FindTypes("types", query, null);
            var exact = matches
                .Where(value =>
                    string.Equals(value.Name, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value.FullName, query, StringComparison.OrdinalIgnoreCase)
                )
                .ToArray();
            return exact.Length == 1
                ? exact[0]
                : matches.Length == 1
                    ? matches[0]
                    : null;
        }

        internal static Type[] FindTypes(string mode, string? type, string? member)
        {
            var normalizedMode = NormalizeMode(mode);
            var typeQuery = type?.Trim() ?? string.Empty;
            var memberQuery = member?.Trim() ?? string.Empty;
            var results = new List<Type>();
            foreach (var candidate in LoadTypes())
            {
                if (!MatchesTypeMode(candidate, normalizedMode)
                    || typeQuery.Length > 0 && !MatchesName(candidate, typeQuery)
                    || memberQuery.Length > 0
                    && !candidate.GetMembers(AllMembers).Any(value => Matches(value.Name, memberQuery)))
                    continue;

                results.Add(candidate);
            }

            return results
                .OrderBy(static value => value.FullName, StringComparer.Ordinal)
                .Take(200)
                .ToArray();
        }

        internal static MemberInfo[] FindMembers(
            string mode,
            string? type,
            string? member)
        {
            var normalizedMode = NormalizeMode(mode);
            return normalizedMode switch
            {
                "fields" => FindMembers<FieldInfo>(normalizedMode, type, member),
                "properties" => FindMembers<PropertyInfo>(normalizedMode, type, member),
                "methods" => FindMembers<MethodInfo>(normalizedMode, type, member),
                "constructors" => FindMembers<ConstructorInfo>(normalizedMode, type, member),
                "members" => FindMembers<MemberInfo>(normalizedMode, type, member),
                _ => throw new InvalidOperationException($"Mode '{mode}' searches types, not members."),
            };
        }

        internal static string Format(string mode, string? type, string? member)
        {
            var normalizedMode = NormalizeMode(mode);
            if (normalizedMode is "types" or "classes" or "structs" or "enums" or "interfaces" or "delegates")
                return string.Join(
                    "\n",
                    FindTypes(normalizedMode, type, member)
                        .Select(static value => $"{FormatTypeKind(value)} {value.FullName} [{value.Assembly.GetName().Name}]")
                );

            return string.Join(
                "\n",
                FindMembers(normalizedMode, type, member)
                    .Select(static value => $"{value.MemberType.ToString().ToLowerInvariant()} {value.DeclaringType?.FullName}.{value}")
            );
        }

        static T[] FindMembers<T>(string mode, string? type, string? member) where T : MemberInfo
        {
            var memberQuery = member?.Trim() ?? string.Empty;
            IEnumerable<Type> types = type is { Length: > 0 }
                ? FindTypes("types", type, null)
                : LoadTypes();
            var results = new List<T>();
            foreach (var candidateType in types)
                foreach (var candidate in candidateType.GetMembers(AllMembers).OfType<T>())
                {
                    if (!MatchesMemberMode(candidate, mode)
                        || memberQuery.Length > 0 && !Matches(candidate.Name, memberQuery))
                        continue;

                    results.Add(candidate);
                    if (results.Count == 200)
                        return results.ToArray();
                }

            return results
                .OrderBy(static value => value.DeclaringType?.FullName, StringComparer.Ordinal)
                .ThenBy(static value => value.Name, StringComparer.Ordinal)
                .ToArray();
        }

        static IEnumerable<Type> LoadTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types.Where(static value => value != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                    yield return type;
            }
        }

        static string NormalizeMode(string mode)
        {
            var normalized = mode?.Trim().ToLowerInvariant() ?? string.Empty;
            return normalized switch
            {
                "type" => "types",
                "class" => "classes",
                "struct" => "structs",
                "enum" => "enums",
                "interface" => "interfaces",
                "delegate" => "delegates",
                "member" => "members",
                "field" => "fields",
                "property" => "properties",
                "method" => "methods",
                "constructor" => "constructors",
                "types" or "classes" or "structs" or "enums" or "interfaces" or "delegates"
                    or "members" or "fields" or "properties" or "methods" or "constructors" => normalized,
                _ => throw new InvalidOperationException($"Unknown reflection mode '{mode}'."),
            };
        }

        static bool MatchesTypeMode(Type type, string mode) =>
            mode switch
            {
                "classes" => type.IsClass && !typeof(Delegate).IsAssignableFrom(type),
                "structs" => type.IsValueType && !type.IsEnum,
                "enums" => type.IsEnum,
                "interfaces" => type.IsInterface,
                "delegates" => typeof(Delegate).IsAssignableFrom(type),
                _ => true,
            };

        static bool MatchesMemberMode(MemberInfo member, string mode) =>
            mode switch
            {
                "fields" => member is FieldInfo,
                "properties" => member is PropertyInfo,
                "methods" => member is MethodInfo,
                "constructors" => member is ConstructorInfo,
                _ => true,
            };

        static bool MatchesName(Type type, string query)
        {
            var comma = query.LastIndexOf(',');
            if (comma >= 0)
                return string.Equals(type.FullName, query.Substring(0, comma).Trim(), StringComparison.Ordinal)
                       && string.Equals(
                           type.Assembly.GetName().Name,
                           query.Substring(comma + 1).Trim(),
                           StringComparison.OrdinalIgnoreCase
                       );

            return Matches(type.FullName ?? type.Name, query) || Matches(type.Name, query);
        }

        static bool Matches(string value, string query) =>
            value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        static T SelectSingle<T>(string kind, IReadOnlyList<T> values)
        {
            if (values.Count == 0)
                throw new InvalidOperationException($"No {kind} matches the reflection query.");
            if (values.Count != 1)
                throw new InvalidOperationException($"The reflection query matches {values.Count} {kind}s.");
            return values[0];
        }

        static string FormatTypeKind(Type type) =>
            type.IsEnum
                ? "enum"
                : type.IsInterface
                    ? "interface"
                    : typeof(Delegate).IsAssignableFrom(type)
                        ? "delegate"
                        : type.IsValueType
                            ? "struct"
                            : "class";
    }
}
