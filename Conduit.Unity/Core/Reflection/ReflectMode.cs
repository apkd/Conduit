#nullable enable

namespace Conduit
{
    readonly struct ReflectMode
    {
        internal readonly ReflectCategory Category;
        internal readonly ReflectTypeKind TypeKind;
        internal readonly ReflectMemberKind MemberKind;

        internal ReflectMode(ReflectCategory category, ReflectTypeKind typeKind, ReflectMemberKind memberKind)
        {
            Category = category;
            TypeKind = typeKind;
            MemberKind = memberKind;
        }
    }

    enum ReflectCategory : byte
    {
        Types,
        Members,
    }

    enum ReflectTypeKind : byte
    {
        Any,
        Class,
        Struct,
        Enum,
        Interface,
        Delegate,
    }

    enum ReflectMemberKind : byte
    {
        None,
        Field,
        Property,
        Method,
        Constructor,
    }

    enum TypeMatchKind : byte
    {
        None,
        Matched,
        Ambiguous,
    }
}
