#nullable enable

using System;
using System.Collections.Generic;

namespace Conduit
{
    /// <summary>Keeps C# type names consistent between formatting and lookup.</summary>
    static class ReflectionTypeAliases
    {
        static readonly Dictionary<Type, string> names = new()
        {
            [typeof(void)] = "void",
            [typeof(bool)] = "bool",
            [typeof(byte)] = "byte",
            [typeof(sbyte)] = "sbyte",
            [typeof(char)] = "char",
            [typeof(decimal)] = "decimal",
            [typeof(double)] = "double",
            [typeof(float)] = "float",
            [typeof(int)] = "int",
            [typeof(uint)] = "uint",
            [typeof(long)] = "long",
            [typeof(ulong)] = "ulong",
            [typeof(object)] = "object",
            [typeof(short)] = "short",
            [typeof(ushort)] = "ushort",
            [typeof(string)] = "string",
            [typeof(IntPtr)] = "nint",
            [typeof(UIntPtr)] = "nuint",
        };
        static readonly Dictionary<string, Type> types = new(StringComparer.OrdinalIgnoreCase);

        static ReflectionTypeAliases()
        {
            foreach (var entry in names)
                types.Add(entry.Value, entry.Key);
        }

        internal static bool TryGetName(Type type, out string name) => names.TryGetValue(type, out name);

        internal static Type? Resolve(string name) => types.TryGetValue(name, out var type) ? type : null;
    }
}
