#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Conduit
{
    static class ReflectionTypeFormatter
    {
        internal static string FormatType(Type type, bool includeNamespace = false)
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

        internal static string DisplayTypeName(Type type, bool includeNamespace)
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

        internal static string JoinTypes(Type[] types, int max)
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
                builder.AppendInvariant(types.Length - shown);
                builder.Append(" more");
            }

            return builder.ToString();
        }

        internal static string TypeKindLabel(Type type)
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

    }
}
