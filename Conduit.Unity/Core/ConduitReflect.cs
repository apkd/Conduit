#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    /// <summary>Provides typed reflection lookup helpers for generated execute_code snippets.</summary>
    public static partial class ConduitReflect
    {
        /// <summary>Finds exactly one reflected type or member using the same modes as the reflect tool.</summary>
        /// <typeparam name="T">
        /// Use <see cref="Type"/> with type modes. Use <see cref="MemberInfo"/> or a concrete member subtype with member modes;
        /// for example, <c>Find&lt;MethodInfo&gt;("members", ...)</c> searches methods.
        /// </typeparam>
        /// <param name="mode">
        /// Search category: <c>types</c>, <c>classes</c>, <c>structs</c>, <c>enums</c>, <c>interfaces</c>,
        /// <c>delegates</c>, <c>members</c>, <c>fields</c>, <c>properties</c>, <c>methods</c>, or <c>constructors</c>.
        /// </param>
        /// <param name="type">
        /// Type-name query. Type modes use it to filter returned types. Member modes resolve it to one containing type first,
        /// then search that type's declared, inherited, and interface members. Short names, full names, substrings, and
        /// <c>Full.Type.Name, AssemblyName</c> are accepted.
        /// </param>
        /// <param name="member">
        /// Member-name query. Member modes use it to filter fields, properties, methods, or constructors by name. Type modes
        /// use it to return types declaring a matching member; for example, <c>FindMany&lt;Type&gt;("enums", member: "AggressiveInlining")</c>.
        /// </param>
        /// <returns>The single reflected type or member.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the mode is invalid, the query is empty, the target is ambiguous, or the result count is not exactly one.</exception>
        /// <remarks>
        /// Provide at least one of <paramref name="type"/> or <paramref name="member"/>. Use the convenience methods such as
        /// <see cref="Type(string?, string?)"/>, <see cref="Methods(string?, string?)"/>, and <see cref="Fields(string?, string?)"/>
        /// when the mode is known at compile time.
        /// </remarks>
        public static T Find<T>(string mode = "types", string? type = null, string? member = null) where T : class
            => FindOneCore<T>(mode, type, member);

        /// <inheritdoc cref="Find{T}(string,string?,string?)" />
        /// <returns>Every reflected type or member matching the query, or an empty array when no result matches.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the mode is invalid, the query is empty, or the containing type is ambiguous.</exception>
        /// <remarks>
        /// This keeps the same filtering and result typing rules as <see cref="Find{T}"/>, but accepts any number of matches.
        /// A member-mode query with a <paramref name="type"/> value still requires that type query to resolve unambiguously.
        /// </remarks>
        public static T[] FindMany<T>(string mode = "types", string? type = null, string? member = null) where T : class
            => FindManyCore<T>(mode, type, member);

        /// <summary>Finds exactly one type.</summary>
        public static Type Type(string? type = null, string? member = null) => Find<Type>("types", type, member);

        /// <summary>Finds types.</summary>
        public static Type[] Types(string? type = null, string? member = null) => FindMany<Type>("types", type, member);

        /// <summary>Finds exactly one class.</summary>
        public static Type Class(string? type = null, string? member = null) => Find<Type>("classes", type, member);

        /// <summary>Finds classes.</summary>
        public static Type[] Classes(string? type = null, string? member = null) => FindMany<Type>("classes", type, member);

        /// <summary>Finds exactly one struct type.</summary>
        public static Type Struct(string? type = null, string? member = null) => Find<Type>("structs", type, member);

        /// <summary>Finds structs.</summary>
        public static Type[] Structs(string? type = null, string? member = null) => FindMany<Type>("structs", type, member);

        /// <summary>Finds exactly one enum type.</summary>
        public static Type Enum(string? type = null, string? member = null) => Find<Type>("enums", type, member);

        /// <summary>Finds enums.</summary>
        public static Type[] Enums(string? type = null, string? member = null) => FindMany<Type>("enums", type, member);

        /// <summary>Finds exactly one interface type.</summary>
        public static Type Interface(string? type = null, string? member = null) => Find<Type>("interfaces", type, member);

        /// <summary>Finds interfaces.</summary>
        public static Type[] Interfaces(string? type = null, string? member = null) => FindMany<Type>("interfaces", type, member);

        /// <summary>Finds exactly one delegate type.</summary>
        public static Type Delegate(string? type = null, string? member = null) => Find<Type>("delegates", type, member);

        /// <summary>Finds delegates.</summary>
        public static Type[] Delegates(string? type = null, string? member = null) => FindMany<Type>("delegates", type, member);

        /// <summary>Finds exactly one type member.</summary>
        public static MemberInfo Member(string? type = null, string? member = null) => Find<MemberInfo>("members", type, member);

        /// <summary>Finds members.</summary>
        public static MemberInfo[] Members(string? type = null, string? member = null) => FindMany<MemberInfo>("members", type, member);

        /// <summary>Finds exactly one field.</summary>
        public static FieldInfo Field(string? type = null, string? member = null) => Find<FieldInfo>("fields", type, member);

        /// <summary>Finds fields.</summary>
        public static FieldInfo[] Fields(string? type = null, string? member = null) => FindMany<FieldInfo>("fields", type, member);

        /// <summary>Finds exactly one property.</summary>
        public static PropertyInfo Property(string? type = null, string? member = null) => Find<PropertyInfo>("properties", type, member);

        /// <summary>Finds properties.</summary>
        public static PropertyInfo[] Properties(string? type = null, string? member = null) => FindMany<PropertyInfo>("properties", type, member);

        /// <summary>Finds exactly one method.</summary>
        public static MethodInfo Method(string? type = null, string? member = null) => Find<MethodInfo>("methods", type, member);

        /// <summary>Finds methods.</summary>
        public static MethodInfo[] Methods(string? type = null, string? member = null) => FindMany<MethodInfo>("methods", type, member);

        /// <summary>Finds exactly one constructor.</summary>
        public static ConstructorInfo Constructor(string? type = null, string? member = null) => Find<ConstructorInfo>("constructors", type, member);

        /// <summary>Finds constructors.</summary>
        public static ConstructorInfo[] Constructors(string? type = null, string? member = null) => FindMany<ConstructorInfo>("constructors", type, member);

        internal static Type? ResolveType(string query)
            => reflect.ResolveTypeName(query);
    }
}
