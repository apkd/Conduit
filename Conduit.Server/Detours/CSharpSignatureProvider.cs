using System.Collections.Immutable;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

sealed class CSharpSignatureProvider(MetadataReader reader) : ISignatureTypeProvider<CSharpType, object?>
{
    static readonly CSharpType voidType = new("void", "void");
    static readonly CSharpType boolType = Value("bool");
    static readonly CSharpType charType = Value("char");
    static readonly CSharpType sbyteType = Value("sbyte");
    static readonly CSharpType byteType = Value("byte");
    static readonly CSharpType shortType = Value("short");
    static readonly CSharpType ushortType = Value("ushort");
    static readonly CSharpType intType = Value("int");
    static readonly CSharpType uintType = Value("uint");
    static readonly CSharpType longType = Value("long");
    static readonly CSharpType ulongType = Value("ulong");
    static readonly CSharpType floatType = Value("float");
    static readonly CSharpType doubleType = Value("double");
    static readonly CSharpType stringType = new("string", "string");
    static readonly CSharpType nintType = Value("nint");
    static readonly CSharpType nuintType = Value("nuint");
    static readonly CSharpType objectType = new("object", "object");
    static readonly CSharpType typedReferenceType = Value("global::System.TypedReference");
    readonly Dictionary<TypeDefinitionHandle, CSharpType> definitionTypes = new();
    readonly Dictionary<(TypeReferenceHandle Handle, byte RawTypeKind), CSharpType> referenceTypes = new();
    readonly Dictionary<(TypeSpecificationHandle Handle, byte RawTypeKind), CSharpType> specificationTypes = new();
    readonly Dictionary<TypeDefinitionHandle, string> metadataTypeNames = new();
    readonly Dictionary<CSharpType, CSharpType> byReferenceTypes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<CSharpType, CSharpType> pointerTypes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<CSharpType, CSharpType> szArrayTypes = new(ReferenceEqualityComparer.Instance);

    public CSharpType GetArrayType(CSharpType elementType, ArrayShape shape)
    {
        var commas = shape.Rank <= 1 ? string.Empty : new string(',', shape.Rank - 1);
        return elementType with
        {
            Source = elementType.Source + "[" + commas + "]",
            BareDisplay = elementType.BareDisplay + "[" + commas + "]",
            IsByRef = false,
        };
    }

    public CSharpType GetByReferenceType(CSharpType elementType)
    {
        if (byReferenceTypes.TryGetValue(elementType, out var cached))
            return cached;

        var type = elementType with { IsByRef = true };
        byReferenceTypes.Add(elementType, type);
        return type;
    }

    public CSharpType GetFunctionPointerType(MethodSignature<CSharpType> signature)
    {
        var callConventions = signature.ReturnType.Modifiers.IsDefault
            ? []
            : signature.ReturnType.Modifiers
                .Where(static modifier => modifier.StartsWith("System.Runtime.CompilerServices.CallConv", StringComparison.Ordinal))
                .Select(static modifier => modifier["System.Runtime.CompilerServices.CallConv".Length..])
                .ToArray();
        var convention = signature.Header.CallingConvention switch
        {
            SignatureCallingConvention.CDecl => " unmanaged[Cdecl]",
            SignatureCallingConvention.StdCall => " unmanaged[Stdcall]",
            SignatureCallingConvention.ThisCall => " unmanaged[Thiscall]",
            SignatureCallingConvention.FastCall => " unmanaged[Fastcall]",
            SignatureCallingConvention.Unmanaged when callConventions.Length > 0 => " unmanaged[" + string.Join(", ", callConventions) + "]",
            SignatureCallingConvention.Unmanaged => " unmanaged",
            SignatureCallingConvention.Default => string.Empty,
            _ => string.Empty,
        };
        var arguments = signature.ParameterTypes
            .Select(RenderFunctionPointerType)
            .Append(signature.ReturnType.ReturnDeclaration);
        var source = "delegate*" + convention + "<" + string.Join(", ", arguments) + ">";
        return new(source, source, UnsupportedReason: signature.Header.CallingConvention == SignatureCallingConvention.VarArgs
            ? "varargs function pointers are not supported"
            : null);

        static string RenderFunctionPointerType(CSharpType type) =>
            type.IsByRef ? (type.IsReadOnly ? "in " : "ref ") + type.Source : type.Source;
    }

    public CSharpType GetGenericInstantiation(CSharpType genericType, ImmutableArray<CSharpType> typeArguments)
    {
        var source = genericType.Source + "<" + string.Join(", ", typeArguments.Select(static type => type.Source)) + ">";
        var display = genericType.BareDisplay + "<" + string.Join(", ", typeArguments.Select(static type => type.BareDisplay)) + ">";
        return new(
            source,
            display,
            IsValueType: genericType.IsValueType,
            UnsupportedReason: genericType.UnsupportedReason
                               ?? typeArguments.Select(static type => type.UnsupportedReason)
                                   .FirstOrDefault(static reason => reason is not null)
        );
    }

    public CSharpType GetGenericMethodParameter(object? genericContext, int index) =>
        new("", "!!" + index, UnsupportedReason: "generic method parameters are not supported");

    public CSharpType GetGenericTypeParameter(object? genericContext, int index) =>
        new("", "!" + index, UnsupportedReason: "generic type parameters are not supported");

    public CSharpType GetModifiedType(CSharpType modifier, CSharpType unmodifiedType, bool isRequired) =>
        unmodifiedType.WithModifier(modifier.BareDisplay, isRequired);

    public CSharpType GetPinnedType(CSharpType elementType) => elementType;

    public CSharpType GetPointerType(CSharpType elementType)
    {
        if (pointerTypes.TryGetValue(elementType, out var cached))
            return cached;

        var type = new CSharpType(
            elementType.Source + "*",
            elementType.BareDisplay + "*",
            UnsupportedReason: elementType.UnsupportedReason
        );
        pointerTypes.Add(elementType, type);
        return type;
    }

    public CSharpType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => voidType,
        PrimitiveTypeCode.Boolean => boolType,
        PrimitiveTypeCode.Char => charType,
        PrimitiveTypeCode.SByte => sbyteType,
        PrimitiveTypeCode.Byte => byteType,
        PrimitiveTypeCode.Int16 => shortType,
        PrimitiveTypeCode.UInt16 => ushortType,
        PrimitiveTypeCode.Int32 => intType,
        PrimitiveTypeCode.UInt32 => uintType,
        PrimitiveTypeCode.Int64 => longType,
        PrimitiveTypeCode.UInt64 => ulongType,
        PrimitiveTypeCode.Single => floatType,
        PrimitiveTypeCode.Double => doubleType,
        PrimitiveTypeCode.String => stringType,
        PrimitiveTypeCode.IntPtr => nintType,
        PrimitiveTypeCode.UIntPtr => nuintType,
        PrimitiveTypeCode.Object => objectType,
        PrimitiveTypeCode.TypedReference => typedReferenceType,
        _ => new("", typeCode.ToString(), UnsupportedReason: $"primitive type '{typeCode}' is unsupported"),
    };

    public CSharpType GetSZArrayType(CSharpType elementType)
    {
        if (szArrayTypes.TryGetValue(elementType, out var cached))
            return cached;

        var type = elementType with
        {
            Source = elementType.Source + "[]",
            BareDisplay = elementType.BareDisplay + "[]",
            IsByRef = false,
        };
        szArrayTypes.Add(elementType, type);
        return type;
    }

    public CSharpType GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        if (definitionTypes.TryGetValue(handle, out var cached))
            return cached;

        var definition = metadataReader.GetTypeDefinition(handle);
        var name = NormalizeIdentifier(metadataReader.GetString(definition.Name), out var unsupported);
        var declaring = definition.GetDeclaringType();
        string metadataName;
        if (declaring.IsNil)
        {
            var @namespace = NormalizeNamespace(
                metadataReader.GetString(definition.Namespace),
                out var namespaceUnsupported
            );
            unsupported ??= namespaceUnsupported;
            metadataName = JoinNamespace(@namespace, name);
        }
        else
        {
            var declaringType = GetTypeFromDefinition(metadataReader, declaring, rawTypeKind);
            unsupported ??= declaringType.UnsupportedReason;
            metadataName = declaringType.Source + "." + name;
        }

        var display = metadataName.StartsWith("global::", StringComparison.Ordinal) ? metadataName[8..] : metadataName;
        var type = new CSharpType(
            metadataName,
            display,
            IsValueType: IsValueType(metadataReader, definition.BaseType),
            UnsupportedReason: unsupported
        );
        definitionTypes.Add(handle, type);
        return type;
    }

    public CSharpType GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var cacheKey = (handle, rawTypeKind);
        if (referenceTypes.TryGetValue(cacheKey, out var cached))
            return cached;

        var definition = metadataReader.GetTypeReference(handle);
        var name = NormalizeIdentifier(metadataReader.GetString(definition.Name), out var unsupported);
        string source;
        if (definition.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var declaringType = GetTypeFromReference(
                metadataReader,
                (TypeReferenceHandle)definition.ResolutionScope,
                rawTypeKind
            );
            unsupported ??= declaringType.UnsupportedReason;
            source = declaringType.Source + "." + name;
        }
        else
        {
            var @namespace = NormalizeNamespace(
                metadataReader.GetString(definition.Namespace),
                out var namespaceUnsupported
            );
            unsupported ??= namespaceUnsupported;
            source = JoinNamespace(@namespace, name);
        }

        var display = source.StartsWith("global::", StringComparison.Ordinal) ? source[8..] : source;
        var type = new CSharpType(
            source,
            display,
            IsValueType: rawTypeKind == (byte)SignatureTypeKind.ValueType,
            UnsupportedReason: unsupported
        );
        referenceTypes.Add(cacheKey, type);
        return type;
    }

    public CSharpType GetTypeFromSpecification(
        MetadataReader metadataReader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
    {
        var cacheKey = (handle, rawTypeKind);
        if (specificationTypes.TryGetValue(cacheKey, out var cached))
            return cached;

        var type = metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        specificationTypes.Add(cacheKey, type);
        return type;
    }

    public string GetMetadataTypeName(TypeDefinitionHandle handle)
    {
        if (metadataTypeNames.TryGetValue(handle, out var cached))
            return cached;

        var definition = reader.GetTypeDefinition(handle);
        var name = StripArity(reader.GetString(definition.Name));
        var declaring = definition.GetDeclaringType();
        var metadataName = declaring.IsNil
            ? string.IsNullOrEmpty(reader.GetString(definition.Namespace))
                ? name
                : reader.GetString(definition.Namespace) + "." + name
            : GetMetadataTypeName(declaring) + "." + name;
        metadataTypeNames.Add(handle, metadataName);
        return metadataName;
    }

    static CSharpType Value(string source) => new(source, source, IsValueType: true);

    static string JoinNamespace(string @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) ? name : "global::" + @namespace + "." + name;

    static string NormalizeIdentifier(string metadataName, out string? unsupported)
    {
        var name = StripArity(metadataName);
        if (!SyntaxFacts.IsValidIdentifier(name))
        {
            unsupported = $"metadata type name '{metadataName}' cannot be represented in C#";
            return name;
        }

        unsupported = null;
        return SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;
    }

    static string NormalizeNamespace(string metadataNamespace, out string? unsupported)
    {
        unsupported = null;
        if (metadataNamespace.Length == 0)
            return string.Empty;

        var segments = metadataNamespace.Split('.');
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = NormalizeIdentifier(segments[index], out var segmentUnsupported);
            unsupported ??= segmentUnsupported;
        }

        return string.Join('.', segments);
    }

    internal static string EscapeMetadataQualifiedName(string name)
        => string.Join('.', name.Split('.').Select(EscapeIdentifier));

    internal static string EscapeIdentifier(string name)
        => SyntaxFacts.IsValidIdentifier(name)
           && SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;

    static string StripArity(string name)
    {
        var separator = name.IndexOf('`');
        return separator < 0 ? name : name[..separator];
    }

    static bool IsValueType(MetadataReader metadataReader, EntityHandle baseType)
    {
        if (baseType.IsNil)
            return false;

        return baseType.Kind switch
        {
            HandleKind.TypeReference => IsValueTypeReference(metadataReader, metadataReader.GetTypeReference((TypeReferenceHandle)baseType)),
            HandleKind.TypeDefinition => IsValueTypeDefinition(metadataReader, metadataReader.GetTypeDefinition((TypeDefinitionHandle)baseType)),
            _ => false,
        };

        static bool IsValueTypeReference(MetadataReader reader, TypeReference definition) =>
            reader.GetString(definition.Namespace) == "System"
            && reader.GetString(definition.Name) is "ValueType" or "Enum";

        static bool IsValueTypeDefinition(MetadataReader reader, TypeDefinition definition) =>
            reader.GetString(definition.Namespace) == "System"
            && reader.GetString(definition.Name) is "ValueType" or "Enum";
    }
}
