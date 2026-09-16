#nullable enable

using System;
using System.Reflection;

namespace Conduit
{
    /// <summary>Identifies inherited declarations without confusing display names with type identity.</summary>
    readonly struct MemberSignature : IEquatable<MemberSignature>
    {
        readonly MemberTypes kind;
        readonly string name;
        readonly Type? returnType;
        readonly int genericArity;
        readonly ParameterInfo[] parameters;

        internal MemberSignature(MemberInfo member)
        {
            kind = member.MemberType;
            name = member.Name;
            returnType = null;
            genericArity = 0;
            parameters = Array.Empty<ParameterInfo>();
            if (member is MethodInfo method)
            {
                // overrides retain their original slot, including covariant return types.
                if (method.IsVirtual)
                    method = method.GetBaseDefinition();
                returnType = method.ReturnType;
                genericArity = method.IsGenericMethod ? method.GetGenericArguments().Length : 0;
                parameters = method.GetParameters();
            }
            else if (member is ConstructorInfo constructor)
                parameters = constructor.GetParameters();
            else if (member is PropertyInfo property)
                parameters = property.GetIndexParameters();
        }

        public bool Equals(MemberSignature other)
        {
            if (kind != other.kind || name != other.name || genericArity != other.genericArity
                || parameters.Length != other.parameters.Length || !SameType(returnType, other.returnType))
                return false;
            for (int index = 0; index < parameters.Length; index++)
                if (!SameType(parameters[index].ParameterType, other.parameters[index].ParameterType))
                    return false;
            return true;
        }

        public override bool Equals(object? value) => value is MemberSignature other && Equals(other);

        // equal signatures can use different generic parameter objects, so their type hashes cannot be included.
        public override int GetHashCode() => HashCode.Combine(kind, name, genericArity, parameters.Length);

        static bool SameType(Type? left, Type? right)
        {
            if (left == right)
                return true;
            if (left is null || right is null)
                return false;
            // renaming a method's generic parameter does not change its signature: M<T>(T) equals M<U>(U).
            if (left.IsGenericParameter || right.IsGenericParameter)
                return left.IsGenericParameter && right.IsGenericParameter
                       && left.DeclaringMethod != null && right.DeclaringMethod != null
                       && left.GenericParameterPosition == right.GenericParameterPosition;
            if (left.HasElementType || right.HasElementType)
                return left.HasElementType && right.HasElementType
                       && left.IsByRef == right.IsByRef && left.IsPointer == right.IsPointer
                       && left.IsArray == right.IsArray
                       && (!left.IsArray || left.GetArrayRank() == right.GetArrayRank()
                           && left.IsSZArray == right.IsSZArray)
                       && SameType(left.GetElementType(), right.GetElementType());
            if (!left.IsGenericType || !right.IsGenericType
                || left.GetGenericTypeDefinition() != right.GetGenericTypeDefinition())
                return false;

            var leftArguments = left.GetGenericArguments();
            var rightArguments = right.GetGenericArguments();
            for (int index = 0; index < leftArguments.Length; index++)
                if (!SameType(leftArguments[index], rightArguments[index]))
                    return false;
            return true;
        }
    }
}
