#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    static partial class ReflectionQueryEngine
    {
        internal static Type? ResolveTypeName(string query)
            => MatchSingleType(LoadIndex(out _), query).Type;

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
            if (type == query.Alias || Contains(info.FullName, query.Text))
                return true;

            if ((query.HasGenericDisplay && info.IsGenericType || query.HasNestedDisplay && info.IsNested)
                && (Contains(
                        ReflectionTypeFormatter.DisplayTypeName(
                            type,
                            includeNamespace: false
                        ),
                        query.Text
                    )
                    || Contains(
                        ReflectionTypeFormatter.DisplayTypeName(
                            type,
                            includeNamespace: true
                        ),
                        query.Text
                    )))
                return true;

            return Contains(info.AssemblyName, query.Text)
                   || query.HasAssembly
                   && Contains($"{info.FullName}, {info.AssemblyName}", query.Text);
        }

        static TypeSearchInfo GetTypeSearchInfo(IReadOnlyList<Type> index, int position)
            => index is TypeIndex typeIndex
                ? typeIndex.SearchInfos[position]
                : GetTypeSearchInfo(index[position]);

        static Dictionary<string, Type?> BuildExactTypeLookup(IReadOnlyList<Type> types)
        {
            var lookup = new Dictionary<string, Type?>(
                types.Count * 3,
                StringComparer.OrdinalIgnoreCase
            );
            AddExactTypes(lookup, types);
            return lookup;
        }

        static Dictionary<string, Type?> GetExactTypeLookup()
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

        static void AddExactTypes(Dictionary<string, Type?> lookup, IReadOnlyList<Type> types)
        {
            for (var position = 0; position < types.Count; position++)
            {
                var type = types[position];
                var info = GetTypeSearchInfo(types, position);
                Add(info.Name);
                if (!string.Equals(info.FullName, info.Name, StringComparison.Ordinal))
                    Add(info.FullName);
                Add($"{info.FullName}, {info.AssemblyName}");

                // copied generic and nested selectors should use the same fast path as CLR type names.
                if (info.IsGenericType || info.IsNested)
                {
                    Add(info.ShortDisplayName ??= ReflectionTypeFormatter.DisplayTypeName(type, includeNamespace: false));
                    Add(ReflectionTypeFormatter.DisplayTypeName(type, includeNamespace: true));
                }

                void Add(string name)
                {
                    if (lookup.TryAdd(name, type))
                        return;

                    if (lookup[name] is { } existing && existing != type)
                        lookup[name] = null;
                }
            }
        }
    }
}
