#nullable enable

namespace Conduit
{
    sealed class TypeSearchInfo
    {
        internal readonly string Name;
        internal readonly string FullName;
        internal readonly string AssemblyName;
        internal readonly bool IsGenericType;
        internal readonly bool IsNested;
        internal readonly ReflectTypeKind Kind;
        internal string? ShortDisplayName;

        internal TypeSearchInfo(
            string name,
            string fullName,
            string assemblyName,
            bool isGenericType,
            bool isNested,
            ReflectTypeKind kind)
        {
            Name = name;
            FullName = fullName;
            AssemblyName = assemblyName;
            IsGenericType = isGenericType;
            IsNested = isNested;
            Kind = kind;
        }
    }
}
