using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

sealed class MethodCatalog
{
    readonly MethodTarget[] methods;

    MethodCatalog(MethodTarget[] methods) => this.methods = methods;

    internal static MethodCatalog Create(IEnumerable<string> referencePaths)
    {
        var methods = new List<MethodTarget>();
        foreach (var path in referencePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    continue;

                var reader = pe.GetMetadataReader();
                if (!reader.IsAssembly)
                    continue;

                var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
                var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
                var provider = new CSharpSignatureProvider(reader);
                foreach (var handle in reader.MethodDefinitions)
                {
                    var definition = reader.GetMethodDefinition(handle);
                    var declaringHandle = definition.GetDeclaringType();
                    var declaringDefinition = reader.GetTypeDefinition(declaringHandle);
                    var declaringType = provider.GetTypeFromDefinition(reader, declaringHandle, 0);
                    var signature = definition.DecodeSignature(provider, genericContext: null);
                    var parameters = GetParameters(reader, definition, signature.ParameterTypes);
                    var name = reader.GetString(definition.Name);
                    var signatureBlob = reader.GetBlobBytes(definition.Signature);
                    var unsupported = GetUnsupportedReason(
                        definition,
                        declaringDefinition,
                        name,
                        signature,
                        declaringType,
                        parameters
                    );
                    var typeName = provider.GetMetadataTypeName(declaringHandle);
                    var canonical = BuildCanonical(assemblyName, typeName, name, signature, parameters);
                    methods.Add(
                        new(
                            mvid,
                            MetadataTokens.GetToken(handle),
                            typeName,
                            declaringType,
                            name,
                            canonical,
                            definition.Attributes,
                            signature.ReturnType,
                            parameters,
                            Convert.ToHexStringLower(SHA256.HashData(signatureBlob)),
                            unsupported
                        )
                    );
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                // unity can report native images and transiently unavailable files alongside managed references.
            }
        }

        return new(methods.ToArray());
    }

    internal MethodResolution Resolve(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return MethodResolution.Failed("`methodName` must identify a method.");

        var matches = methods
            .Where(method => method.Matches(selector))
            .ToArray();
        if (matches.Length == 0)
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");

        var supported = matches.Where(static method => method.UnsupportedReason is null).ToArray();
        if (supported.Length == 1)
            return MethodResolution.Succeeded(supported[0]);

        if (supported.Length == 0)
        {
            var reasons = matches
                .Select(method => $"{method.CanonicalSelector}: {method.UnsupportedReason}")
                .Distinct(StringComparer.Ordinal);
            return MethodResolution.Failed(string.Join("\n", reasons));
        }

        return MethodResolution.Ambiguous(
            "Method selector is ambiguous. Use one of:\n" + string.Join("\n", supported.Select(static method => method.CanonicalSelector))
        );
    }

    static ImmutableArray<MethodParameter> GetParameters(
        MetadataReader reader,
        MethodDefinition definition,
        ImmutableArray<CSharpType> signatureTypes)
    {
        var definitions = definition.GetParameters()
            .Select(handle => (Handle: handle, Definition: reader.GetParameter(handle)))
            .Where(static parameter => parameter.Definition.SequenceNumber > 0)
            .OrderBy(static parameter => parameter.Definition.SequenceNumber)
            .ToArray();
        var builder = ImmutableArray.CreateBuilder<MethodParameter>(signatureTypes.Length);
        for (int index = 0; index < signatureTypes.Length; ++index)
        {
            var attributes = index < definitions.Length ? definitions[index].Definition.Attributes : 0;
            var isRefReadonly = index < definitions.Length
                                && HasAttribute(
                                    reader,
                                    definitions[index].Handle,
                                    "System.Runtime.CompilerServices",
                                    "RequiresLocationAttribute"
                                );
            builder.Add(new(signatureTypes[index], attributes, isRefReadonly));
        }

        return builder.MoveToImmutable();
    }

    static bool HasAttribute(
        MetadataReader reader,
        EntityHandle parent,
        string expectedNamespace,
        string expectedName)
    {
        foreach (var handle in reader.GetCustomAttributes(parent))
        {
            var attribute = reader.GetCustomAttribute(handle);
            var type = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default,
            };
            if (type.IsNil)
                continue;

            string @namespace;
            string name;
            if (type.Kind == HandleKind.TypeReference)
            {
                var reference = reader.GetTypeReference((TypeReferenceHandle)type);
                @namespace = reader.GetString(reference.Namespace);
                name = reader.GetString(reference.Name);
            }
            else if (type.Kind == HandleKind.TypeDefinition)
            {
                var definition = reader.GetTypeDefinition((TypeDefinitionHandle)type);
                @namespace = reader.GetString(definition.Namespace);
                name = reader.GetString(definition.Name);
            }
            else
                continue;

            if (@namespace == expectedNamespace && name == expectedName)
                return true;
        }

        return false;
    }

    static string? GetUnsupportedReason(
        MethodDefinition method,
        TypeDefinition declaringType,
        string name,
        MethodSignature<CSharpType> signature,
        CSharpType declaringTypeSyntax,
        ImmutableArray<MethodParameter> parameters)
    {
        if (name is ".ctor" or ".cctor")
            return "constructors are not supported";
        if (method.GetGenericParameters().Count > 0 || declaringType.GetGenericParameters().Count > 0)
            return "generic methods and methods declared on generic types are not supported";
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            return "P/Invoke methods are not supported";
        if ((method.ImplAttributes & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native)) != 0)
            return "runtime, native, and InternalCall methods are not supported";
        if ((method.Attributes & MethodAttributes.Abstract) != 0 || method.RelativeVirtualAddress == 0)
            return "the method has no managed implementation body";
        if (signature.Header.CallingConvention == SignatureCallingConvention.VarArgs)
            return "varargs methods are not supported";
        if ((method.Attributes & MethodAttributes.Static) == 0 && declaringTypeSyntax.UnsupportedReason is { } declaringReason)
            return declaringReason;
        if (signature.ReturnType.UnsupportedReason is { } returnReason)
            return returnReason;
        foreach (var parameter in parameters)
            if (parameter.Type.UnsupportedReason is { } parameterReason)
                return parameterReason;
        return null;
    }

    static string BuildCanonical(
        string assemblyName,
        string typeName,
        string methodName,
        MethodSignature<CSharpType> signature,
        ImmutableArray<MethodParameter> parameters) =>
        $"{assemblyName}::{typeName}.{methodName}("
        + string.Join(",", parameters.Select(static parameter => parameter.Display))
        + $")->{signature.ReturnType.ReturnDisplay}";
}

sealed record MethodTarget(
    Guid ModuleVersionId,
    int MetadataToken,
    string DeclaringTypeName,
    CSharpType DeclaringType,
    string MethodName,
    string CanonicalSelector,
    MethodAttributes Attributes,
    CSharpType ReturnType,
    ImmutableArray<MethodParameter> Parameters,
    string SignatureHash,
    string? UnsupportedReason)
{
    public bool IsStatic => (Attributes & MethodAttributes.Static) != 0;

    public bool Matches(string selector)
    {
        if (string.Equals(selector, CanonicalSelector, StringComparison.Ordinal))
            return true;

        var simple = DeclaringTypeName + "." + MethodName;
        var escapedSimple = CSharpSignatureProvider.EscapeMetadataQualifiedName(DeclaringTypeName)
                            + "."
                            + CSharpSignatureProvider.EscapeMetadataQualifiedName(MethodName);
        if (string.Equals(selector, simple, StringComparison.Ordinal)
            || string.Equals(selector, escapedSimple, StringComparison.Ordinal))
            return true;

        var typeName = DeclaringTypeName[(DeclaringTypeName.LastIndexOf('.') + 1)..];
        var shortSelector = typeName + "." + MethodName;
        var escapedShortSelector = CSharpSignatureProvider.EscapeIdentifier(typeName)
                                   + "."
                                   + CSharpSignatureProvider.EscapeMetadataQualifiedName(MethodName);
        return string.Equals(selector, shortSelector, StringComparison.Ordinal)
               || string.Equals(selector, escapedShortSelector, StringComparison.Ordinal);
    }

    public string ReplacementDeclaration
    {
        get
        {
            var parameters = new List<string>();
            if (!IsStatic)
            {
                var receiver = DeclaringType.IsValueType ? "ref " : string.Empty;
                parameters.Add(receiver + DeclaringType.Source + " @this");
            }

            for (int index = 0; index < Parameters.Length; ++index)
                parameters.Add(Parameters[index].Declaration("arg" + index));

            return $"public static unsafe {ReturnType.ReturnDeclaration} Replace({string.Join(", ", parameters)})";
        }
    }
}

readonly record struct MethodParameter(
    CSharpType Type,
    ParameterAttributes Attributes,
    bool IsRefReadonly)
{
    public string Display => Prefix + Type.BareDisplay;

    public string Declaration(string name) => Prefix + Type.Source + " " + name;

    string Prefix => (Type.IsByRef, Attributes) switch
    {
        (false, _) => string.Empty,
        _ when IsRefReadonly || Type.HasModifier("System.Runtime.CompilerServices.RequiresLocationAttribute") => "ref readonly ",
        (_, var attributes) when (attributes & ParameterAttributes.Out) != 0 => "out ",
        (_, var attributes) when (attributes & ParameterAttributes.In) != 0 || Type.IsReadOnly => "in ",
        _ => "ref ",
    };
}

readonly record struct MethodResolution(MethodTarget? Target, string? Outcome, string? Diagnostic)
{
    internal static MethodResolution Succeeded(MethodTarget target) => new(target, null, null);
    internal static MethodResolution Failed(string diagnostic) => new(null, ToolOutcome.Exception, diagnostic);
    internal static MethodResolution Ambiguous(string diagnostic) => new(null, ToolOutcome.AmbiguousTarget, diagnostic);
}

sealed record CSharpType(
    string Source,
    string BareDisplay,
    bool IsByRef = false,
    bool IsReadOnly = false,
    bool IsValueType = false,
    ImmutableArray<string> Modifiers = default,
    string? UnsupportedReason = null)
{
    public string ReturnDeclaration => (IsByRef, IsReadOnly) switch
    {
        (true, true) => "ref readonly " + Source,
        (true, false) => "ref " + Source,
        _ => Source,
    };

    public string ReturnDisplay => (IsByRef, IsReadOnly) switch
    {
        (true, true) => "ref readonly " + BareDisplay,
        (true, false) => "ref " + BareDisplay,
        _ => BareDisplay,
    };

    public bool HasModifier(string modifier)
        => !Modifiers.IsDefault && Modifiers.Contains(modifier, StringComparer.Ordinal);

    public CSharpType WithModifier(string modifier, bool required)
    {
        var modifiers = Modifiers.IsDefault ? ImmutableArray<string>.Empty : Modifiers;
        bool isReadOnly = modifier is "System.Runtime.CompilerServices.IsReadOnlyAttribute"
            or "System.Runtime.InteropServices.InAttribute"
            or "System.Runtime.CompilerServices.RequiresLocationAttribute";
        bool supported = isReadOnly
                         || modifier.StartsWith(
                             "System.Runtime.CompilerServices.CallConv",
                             StringComparison.Ordinal
                         );
        return this with
        {
            IsReadOnly = IsReadOnly || isReadOnly,
            Modifiers = modifiers.Add(modifier),
            UnsupportedReason = UnsupportedReason ?? (required && !supported
                ? $"required custom modifier '{modifier}' cannot be represented exactly in C#"
                : null),
        };
    }
}

sealed class CSharpSignatureProvider(MetadataReader reader) : ISignatureTypeProvider<CSharpType, object?>
{
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

    public CSharpType GetByReferenceType(CSharpType elementType) => elementType with { IsByRef = true };

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

    public CSharpType GetPointerType(CSharpType elementType) =>
        new(elementType.Source + "*", elementType.BareDisplay + "*", UnsupportedReason: elementType.UnsupportedReason);

    public CSharpType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => new("void", "void"),
        PrimitiveTypeCode.Boolean => Value("bool"),
        PrimitiveTypeCode.Char => Value("char"),
        PrimitiveTypeCode.SByte => Value("sbyte"),
        PrimitiveTypeCode.Byte => Value("byte"),
        PrimitiveTypeCode.Int16 => Value("short"),
        PrimitiveTypeCode.UInt16 => Value("ushort"),
        PrimitiveTypeCode.Int32 => Value("int"),
        PrimitiveTypeCode.UInt32 => Value("uint"),
        PrimitiveTypeCode.Int64 => Value("long"),
        PrimitiveTypeCode.UInt64 => Value("ulong"),
        PrimitiveTypeCode.Single => Value("float"),
        PrimitiveTypeCode.Double => Value("double"),
        PrimitiveTypeCode.String => new("string", "string"),
        PrimitiveTypeCode.IntPtr => Value("nint"),
        PrimitiveTypeCode.UIntPtr => Value("nuint"),
        PrimitiveTypeCode.Object => new("object", "object"),
        PrimitiveTypeCode.TypedReference => Value("global::System.TypedReference"),
        _ => new("", typeCode.ToString(), UnsupportedReason: $"primitive type '{typeCode}' is unsupported"),
    };

    public CSharpType GetSZArrayType(CSharpType elementType) => elementType with
    {
        Source = elementType.Source + "[]",
        BareDisplay = elementType.BareDisplay + "[]",
        IsByRef = false,
    };

    public CSharpType GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
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
        return new(
            metadataName,
            display,
            IsValueType: IsValueType(metadataReader, definition.BaseType),
            UnsupportedReason: unsupported
        );
    }

    public CSharpType GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind)
    {
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
        return new(source, display, IsValueType: rawTypeKind == (byte)SignatureTypeKind.ValueType, UnsupportedReason: unsupported);
    }

    public CSharpType GetTypeFromSpecification(
        MetadataReader metadataReader,
        object? genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind) =>
        metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetMetadataTypeName(TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = StripArity(reader.GetString(definition.Name));
        var declaring = definition.GetDeclaringType();
        return declaring.IsNil
            ? string.IsNullOrEmpty(reader.GetString(definition.Namespace))
                ? name
                : reader.GetString(definition.Namespace) + "." + name
            : GetMetadataTypeName(declaring) + "." + name;
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
