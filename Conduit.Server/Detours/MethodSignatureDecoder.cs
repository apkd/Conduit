using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Conduit;

static class MethodSignatureDecoder
{
    internal static DecodedMethodSignature DecodeMethodSignature(string path, int metadataToken)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var definition = reader.GetMethodDefinition(
            MetadataTokens.MethodDefinitionHandle(metadataToken & 0x00ff_ffff)
        );
        var provider = new CSharpSignatureProvider(reader);
        var declaringType = provider.GetTypeFromDefinition(
            reader,
            definition.GetDeclaringType(),
            0
        );
        var signature = definition.DecodeSignature(provider, genericContext: null);
        var parameters = GetParameters(reader, definition, signature.ParameterTypes);
        return new(
            declaringType,
            signature.ReturnType,
            parameters,
            GetSignatureUnsupportedReason(
                definition,
                declaringType,
                signature,
                parameters
            )
        );
    }

    internal static byte[] ReadMethodSignatureBlob(string path, int metadataToken)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var definition = reader.GetMethodDefinition(
            MetadataTokens.MethodDefinitionHandle(metadataToken & 0x00ff_ffff)
        );
        return reader.GetBlobBytes(definition.Signature);
    }

    static ImmutableArray<MethodParameter> GetParameters(
        MetadataReader reader,
        MethodDefinition definition,
        ImmutableArray<CSharpType> signatureTypes)
    {
        if (signatureTypes.IsEmpty)
            return ImmutableArray<MethodParameter>.Empty;

        Span<ParameterHandle> definitions = signatureTypes.Length <= 16
            ? stackalloc ParameterHandle[signatureTypes.Length]
            : new ParameterHandle[signatureTypes.Length];
        definitions.Clear();
        foreach (var handle in definition.GetParameters())
        {
            var sequenceNumber = reader.GetParameter(handle).SequenceNumber;
            if (sequenceNumber > 0 && sequenceNumber <= definitions.Length)
                definitions[sequenceNumber - 1] = handle;
        }

        var parameters = new MethodParameter[signatureTypes.Length];
        for (int index = 0; index < signatureTypes.Length; ++index)
        {
            var parameterHandle = definitions[index];
            var attributes = parameterHandle.IsNil
                ? 0
                : reader.GetParameter(parameterHandle).Attributes;
            var isRefReadonly = !parameterHandle.IsNil
                                && HasAttribute(
                                    reader,
                                    parameterHandle,
                                    "System.Runtime.CompilerServices",
                                    "RequiresLocationAttribute"
                                );
            parameters[index] = new(signatureTypes[index], attributes, isRefReadonly);
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(parameters);
    }

    internal readonly record struct DecodedMethodSignature(
        CSharpType DeclaringType,
        CSharpType ReturnType,
        ImmutableArray<MethodParameter> Parameters,
        string? UnsupportedReason
    );

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

    static string? GetSignatureUnsupportedReason(
        MethodDefinition method,
        CSharpType declaringTypeSyntax,
        MethodSignature<CSharpType> signature,
        ImmutableArray<MethodParameter> parameters)
    {
        if ((method.Attributes & MethodAttributes.Static) == 0 && declaringTypeSyntax.UnsupportedReason is { } declaringReason)
            return declaringReason;
        if (signature.ReturnType.UnsupportedReason is { } returnReason)
            return returnReason;
        foreach (var parameter in parameters)
            if (parameter.Type.UnsupportedReason is { } parameterReason)
                return parameterReason;
        return null;
    }
}
