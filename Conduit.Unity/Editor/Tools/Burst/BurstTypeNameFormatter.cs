#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Conduit
{
    static class BurstTypeNameFormatter
    {
        static readonly Regex builtInTypeName = new(@"\b(?:System\.)?(?:Void|Boolean|Byte|SByte|Char|Decimal|Double|Single|Int32|UInt32|Int64|UInt64|Int16|UInt16|Object|String|IntPtr|UIntPtr)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex qualifiedTypeName = new(@"\b(?:[A-Z_][A-Za-z0-9_]*\.)+[A-Z_][A-Za-z0-9_]*(?:\+[A-Z_][A-Za-z0-9_]*)*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        static readonly Regex unityMathematicsName = new(@"\bUnity\.Mathematics\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string SimplifyMetadataGenerics(string symbol)
        {
            if (symbol.IndexOf('`') < 0)
                return symbol;

            using var pooledBuilder = ConduitPool.GetStringBuilder(out var builder);
            builder.EnsureCapacity(symbol.Length);
            for (var i = 0; i < symbol.Length; i++)
            {
                if (symbol[i] != '`' || !TryReadGenericArity(symbol, i + 1, out var afterArity))
                {
                    builder.Append(symbol[i]);
                    continue;
                }

                if (TryReadMetadataGenericArguments(symbol, afterArity, out var afterArguments, out var arguments)
                    || TryReadSimpleGenericArguments(symbol, afterArity, out afterArguments, out arguments))
                {
                    builder.Append('<');
                    for (var argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
                    {
                        if (argumentIndex > 0)
                            builder.Append(',');

                        builder.Append(SimplifyMetadataGenerics(arguments[argumentIndex]));
                    }

                    builder.Append('>');
                    i = afterArguments - 1;
                    continue;
                }

                i = afterArity - 1;
            }

            return builder.ToString();
        }

        static bool TryReadGenericArity(string symbol, int start, out int end)
        {
            end = start;
            while (end < symbol.Length && char.IsDigit(symbol[end]))
                end++;

            return end > start;
        }

        static bool TryReadMetadataGenericArguments(string symbol, int start, out int end, out List<string> arguments)
        {
            end = start;
            arguments = new();
            if (start + 1 >= symbol.Length || symbol[start] != '[' || symbol[start + 1] != '[')
                return false;

            var index = start + 1;
            while (index < symbol.Length && symbol[index] == '[')
            {
                var argumentStart = ++index;
                var depth = 0;
                while (index < symbol.Length)
                {
                    if (symbol[index] == '[')
                    {
                        depth++;
                    }
                    else if (symbol[index] == ']')
                    {
                        if (depth == 0)
                            break;

                        depth--;
                    }

                    index++;
                }

                if (index >= symbol.Length)
                    return false;

                arguments.Add(symbol[argumentStart..index]);
                index++;
                if (index < symbol.Length && symbol[index] == ',')
                {
                    index++;
                    continue;
                }

                if (index < symbol.Length && symbol[index] == ']')
                {
                    end = index + 1;
                    return true;
                }

                return false;
            }

            return false;
        }

        static bool TryReadSimpleGenericArguments(string symbol, int start, out int end, out List<string> arguments)
        {
            end = start;
            arguments = new();
            if (start >= symbol.Length || symbol[start] != '[')
                return false;

            var argumentStart = start + 1;
            var depth = 0;
            for (var i = argumentStart; i < symbol.Length; i++)
            {
                if (symbol[i] == '[')
                {
                    depth++;
                    continue;
                }

                if (symbol[i] == ']')
                {
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }

                    arguments.Add(symbol[argumentStart..i]);
                    end = i + 1;
                    return true;
                }

                if (symbol[i] != ',' || depth != 0)
                    continue;

                arguments.Add(symbol[argumentStart..i]);
                argumentStart = i + 1;
            }

            return false;
        }

        internal static string ReplaceBuiltInTypeNames(string symbol) =>
            builtInTypeName.Replace(symbol, match => BuiltInAlias(match.Value));

        internal static string StripCommonLowercaseTypeNamespaces(string symbol) =>
            unityMathematicsName.Replace(symbol, "$1");

        static string BuiltInAlias(string typeName)
        {
            if (typeName.StartsWith("System.", StringComparison.Ordinal))
                typeName = typeName["System.".Length..];

            return typeName switch
            {
                "Void"    => "void",
                "Boolean" => "bool",
                "Byte"    => "byte",
                "SByte"   => "sbyte",
                "Char"    => "char",
                "Decimal" => "decimal",
                "Double"  => "double",
                "Single"  => "float",
                "Int32"   => "int",
                "UInt32"  => "uint",
                "Int64"   => "long",
                "UInt64"  => "ulong",
                "Int16"   => "short",
                "UInt16"  => "ushort",
                "Object"  => "object",
                "String"  => "string",
                "IntPtr"  => "nint",
                "UIntPtr" => "nuint",
                _         => typeName,
            };
        }

        internal static string StripNamespaces(string symbol)
        {
            var names = new List<string>();
            foreach (Match match in qualifiedTypeName.Matches(symbol))
                names.Add(match.Value);

            if (names.Count == 0)
                return symbol;

            var commonPrefix = CommonNamespacePrefix(names);
            return qualifiedTypeName.Replace(symbol, match =>
            {
                var name = match.Value;
                return commonPrefix.Length > 0 && name.StartsWith(commonPrefix, StringComparison.Ordinal)
                    ? name[commonPrefix.Length..]
                    : ShortTypeName(name);
            });
        }

        static string CommonNamespacePrefix(IReadOnlyList<string> typeNames)
        {
            if (typeNames.Count < 2)
                return string.Empty;

            string[]? common = null;
            foreach (var typeName in typeNames)
            {
                var segments = NamespaceSegments(typeName);
                if (segments.Length == 0)
                    continue;

                if (common == null)
                {
                    common = segments;
                    continue;
                }

                var shared = 0;
                var length = Math.Min(common.Length, segments.Length);
                while (shared < length && common[shared] == segments[shared])
                    shared++;

                if (shared == 0)
                    return string.Empty;

                if (shared == common.Length)
                    continue;

                var reduced = new string[shared];
                Array.Copy(common, reduced, shared);
                common = reduced;
            }

            if (common is not { Length: > 0 } || IsBroadRootNamespace(common))
                return string.Empty;

            return string.Join(".", common) + ".";
        }

        static bool IsBroadRootNamespace(string[] segments) =>
            segments.Length == 1 && segments[0] is "Unity" or "System" or "Microsoft";

        static string[] NamespaceSegments(string typeName)
        {
            var dot = typeName.LastIndexOf('.');
            return dot < 0 ? Array.Empty<string>() : typeName[..dot].Split('.');
        }

        internal static string ShortTypeName(string typeName)
        {
            var nestedIndex = typeName.IndexOf('+');
            var searchEnd = nestedIndex < 0 ? typeName.Length - 1 : nestedIndex - 1;
            var dot = typeName.LastIndexOf('.', searchEnd);
            return dot < 0 ? typeName : typeName[(dot + 1)..];
        }
    }
}
