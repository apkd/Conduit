#nullable enable

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Conduit
{
    static partial class ReflectionMemberFormatter
    {
        static readonly ConcurrentDictionary<MemberInfo, string> memberSignatureCache = new();

        internal static MemberDisplay FormatMemberMatch(MemberMatch match)
        {
            var method = match.Member as MethodBase;
            return new(
                match.DeclaringType,
                match.Kind,
                match.Name,
                FormatMemberSignature(match),
                match.MatchRank,
                method != null && MethodDetourSupport.GetUnsupportedReason(method) != null,
                method is MethodInfo methodInfo && IsExtern(methodInfo)
            );
        }

        static string FormatMemberSignature(MemberMatch match)
            => FormatMemberSignature(match.Member);

        internal static string FormatMemberSignature(MemberInfo member)
            => memberSignatureCache.GetOrAdd(member, static value => value switch
            {
                FieldInfo field             => FormatField(field),
                PropertyInfo property       => FormatProperty(property),
                MethodInfo method           => FormatMethod(method),
                ConstructorInfo constructor => FormatConstructor(constructor),
                _                           => value.ToString() ?? value.Name,
            });

        static string FormatField(FieldInfo field)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendFieldAccess(builder, field);
            AppendFieldModifiers(builder, field);
            builder.Append(ReflectionTypeFormatter.FormatType(field.FieldType));
            builder.Append(' ');
            builder.Append(CSharpIdentifier.Escape(field.Name));
            return builder.ToString();
        }

        static string FormatProperty(PropertyInfo property)
        {
            var accessor = PrimaryAccessor(property);
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
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

            builder.Append(ReflectionTypeFormatter.FormatType(propertyType));
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
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            AppendAccess(builder, method);
            AppendMethodModifiers(builder, method);
            AppendReturnType(builder, method);
            builder.Append(' ');
            builder.Append(CSharpIdentifier.EscapeQualified(method.Name));
            AppendGenericArguments(builder, method.GetGenericArguments());
            builder.Append('(');
            builder.Append(FormatParameters(method.GetParameters()));
            builder.Append(')');
            return builder.ToString();
        }

        static string FormatConstructor(ConstructorInfo constructor)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
            if (constructor.IsStatic)
                builder.Append("static ");
            else
                AppendAccess(builder, constructor);

            builder.Append(
                constructor.DeclaringType == null
                    ? ".ctor"
                    : ReflectionTypeFormatter.DisplayTypeName(
                        constructor.DeclaringType,
                        includeNamespace: false
                    )
            );
            builder.Append('(');
            builder.Append(FormatParameters(constructor.GetParameters()));
            builder.Append(')');
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

            builder.Append(ReflectionTypeFormatter.FormatType(returnType));
        }

        static string FormatParameters(ParameterInfo[] parameters)
        {
            using var pooledBuilder = BridgeStringBuilderPool.Rent(out var builder);
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

            builder.Append(ReflectionTypeFormatter.FormatType(parameterType));
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

        internal static string MemberKindHeader(ReflectMemberKind kind)
            => kind switch
            {
                ReflectMemberKind.Field       => "Fields",
                ReflectMemberKind.Property    => "Properties",
                ReflectMemberKind.Method      => "Methods",
                ReflectMemberKind.Constructor => "Constructors",
                _                             => "Members",
            };

        internal static int CompareMembers(MemberDisplay left, MemberDisplay right)
        {
            var rank = left.MatchRank.CompareTo(right.MatchRank);
            if (rank != 0)
                return rank;

            var type = ReflectionQueryEngine.CompareTypes(left.DeclaringType, right.DeclaringType);
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

        static int CompareMemberMatches(MemberMatch left, MemberMatch right)
        {
            var rank = left.MatchRank.CompareTo(right.MatchRank);
            if (rank != 0)
                return rank;

            var type = ReflectionQueryEngine.CompareTypes(left.DeclaringType, right.DeclaringType);
            if (type != 0)
                return type;

            var kind = left.Kind.CompareTo(right.Kind);
            return kind != 0
                ? kind
                : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        }

        internal static int CompareMemberMatchesWithSignatures(MemberMatch left, MemberMatch right)
        {
            var comparison = CompareMemberMatches(left, right);
            return comparison != 0
                ? comparison
                : string.Compare(
                    FormatMemberSignature(left),
                    FormatMemberSignature(right),
                    StringComparison.Ordinal
                );
        }
    }
}
