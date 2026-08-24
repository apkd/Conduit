#nullable enable

using System;
using System.Reflection;
using System.Text;

namespace Conduit
{
    static partial class ReflectionMemberFormatter
    {
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

    }
}
