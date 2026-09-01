using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Conduit;

sealed class MethodTarget
{
    readonly MethodCatalog.DeclaringTypeInfo declaringType;
    readonly MetadataUnsupportedReason metadataUnsupportedReason;
    readonly bool isStatic;
    SignatureState? signature;

    internal MethodTarget(
        MethodCatalog.DeclaringTypeInfo declaringType,
        int metadataToken,
        string methodName,
        bool isStatic,
        MetadataUnsupportedReason unsupportedReason)
    {
        this.declaringType = declaringType;
        this.isStatic = isStatic;
        MetadataToken = metadataToken;
        MethodName = methodName;
        metadataUnsupportedReason = unsupportedReason;
    }

    internal Guid ModuleVersionId => declaringType.Assembly.ModuleVersionId;
    internal int MetadataToken { get; }
    internal string AssemblyName => declaringType.Assembly.Name;
    internal string DeclaringTypeName => declaringType.Name;
    internal string MethodName { get; }
    internal string AssemblyPath => declaringType.Assembly.Path;

    internal CSharpType DeclaringType
    {
        get
        {
            return GetSignature().DeclaringType;
        }
    }

    internal CSharpType ReturnType
    {
        get
        {
            return GetSignature().ReturnType;
        }
    }

    internal ImmutableArray<MethodParameter> Parameters
    {
        get
        {
            return GetSignature().Parameters;
        }
    }

    internal string? UnsupportedReason
    {
        get
        {
            if (metadataUnsupportedReason != MetadataUnsupportedReason.None)
                return metadataUnsupportedReason switch
                {
                    MetadataUnsupportedReason.Constructor => "constructors are not supported",
                    MetadataUnsupportedReason.Generic => "generic methods and methods declared on generic types are not supported",
                    MetadataUnsupportedReason.PInvoke => "P/Invoke methods are not supported",
                    MetadataUnsupportedReason.Runtime => "runtime, native, and InternalCall methods are not supported",
                    MetadataUnsupportedReason.NoBody => "the method has no managed implementation body",
                    MetadataUnsupportedReason.VarArgs => "varargs methods are not supported",
                    _ => throw new InvalidOperationException("Unknown method support state."),
                };

            return GetSignature().UnsupportedReason;
        }
    }

    internal bool IsStatic => isStatic;

    internal string CanonicalSelector
    {
        get
        {
            var state = GetSignature();
            if (state.CanonicalSelector is not null)
                return state.CanonicalSelector;

            var canonical =
                $"{AssemblyName}::{DeclaringTypeName}.{MethodName}("
                + string.Join(",", state.Parameters.Select(static parameter => parameter.Display))
                + $")->{state.ReturnType.ReturnDisplay}";
            return Interlocked.CompareExchange(ref state.CanonicalSelector, canonical, null)
                   ?? canonical;
        }
    }

    internal string SignatureHash
    {
        get
        {
            var state = GetSignature();
            if (state.SignatureHash is not null)
                return state.SignatureHash;

            var hash = Convert.ToHexStringLower(SHA256.HashData(
                MethodSignatureDecoder.ReadMethodSignatureBlob(AssemblyPath, MetadataToken)
            ));
            return Interlocked.CompareExchange(ref state.SignatureHash, hash, null)
                   ?? hash;
        }
    }

    internal bool Matches(string selector)
    {
        var assemblySeparator = selector.IndexOf("::", StringComparison.Ordinal);
        if (assemblySeparator >= 0)
        {
            if (!selector.AsSpan(0, assemblySeparator).SequenceEqual(AssemblyName))
                return false;

            var memberOffset = assemblySeparator + 2;
            var expectedLength = DeclaringTypeName.Length + MethodName.Length + 2;
            if (selector.Length < memberOffset + expectedLength
                || !selector.AsSpan(memberOffset, DeclaringTypeName.Length).SequenceEqual(DeclaringTypeName)
                || selector[memberOffset + DeclaringTypeName.Length] != '.'
                || !selector.AsSpan(
                        memberOffset + DeclaringTypeName.Length + 1,
                        MethodName.Length
                    ).SequenceEqual(MethodName)
                || selector[memberOffset + expectedLength - 1] != '(')
                return false;

            if (string.Equals(selector, CanonicalSelector, StringComparison.Ordinal))
                return true;

            return MethodSelectorSignatureComparer.Equals(
                selector.AsSpan(memberOffset + expectedLength - 1),
                CanonicalSelector.AsSpan(memberOffset + expectedLength - 1)
            );
        }

        if (MatchesCompositeSelector(selector, DeclaringTypeName, 0, MethodName))
            return true;

        var shortNameOffset = DeclaringTypeName.LastIndexOf('.') + 1;
        if (MatchesCompositeSelector(selector, DeclaringTypeName, shortNameOffset, MethodName))
            return true;

        if (selector.IndexOf('@') < 0)
            return false;

        var escapedSimple = CSharpSignatureProvider.EscapeMetadataQualifiedName(DeclaringTypeName)
                            + "."
                            + CSharpSignatureProvider.EscapeMetadataQualifiedName(MethodName);
        if (string.Equals(selector, escapedSimple, StringComparison.Ordinal))
            return true;

        var typeName = DeclaringTypeName[shortNameOffset..];
        var escapedShortSelector = CSharpSignatureProvider.EscapeIdentifier(typeName)
                                   + "."
                                   + CSharpSignatureProvider.EscapeMetadataQualifiedName(MethodName);
        return string.Equals(selector, escapedShortSelector, StringComparison.Ordinal);

        static bool MatchesCompositeSelector(
            string selector,
            string declaringTypeName,
            int typeNameOffset,
            string methodName)
        {
            var typeName = declaringTypeName.AsSpan(typeNameOffset);
            return selector.Length == typeName.Length + methodName.Length + 1
                   && selector.AsSpan(0, typeName.Length).SequenceEqual(typeName)
                   && selector[typeName.Length] == '.'
                   && selector.AsSpan(typeName.Length + 1).SequenceEqual(methodName);
        }
    }

    internal string ReplacementDeclaration
    {
        get
        {
            var state = GetSignature();
            var parameters = new List<string>(state.Parameters.Length + (IsStatic ? 0 : 1));
            if (!IsStatic)
            {
                var receiver = state.DeclaringType.IsValueType ? "ref " : string.Empty;
                parameters.Add(receiver + state.DeclaringType.Source + " @this");
            }

            for (int index = 0; index < state.Parameters.Length; ++index)
                parameters.Add(state.Parameters[index].Declaration("arg" + index));

            return $"public static unsafe {state.ReturnType.ReturnDeclaration} Replace({string.Join(", ", parameters)})";
        }
    }

    SignatureState GetSignature()
    {
        if (Volatile.Read(ref signature) is { } decoded)
            return decoded;

        lock (this)
        {
            if (signature is not null)
                return signature;

            var value = MethodSignatureDecoder.DecodeMethodSignature(AssemblyPath, MetadataToken);
            var state = new SignatureState(
                value.DeclaringType,
                value.ReturnType,
                value.Parameters,
                value.UnsupportedReason
            );
            Volatile.Write(ref signature, state);
            return state;
        }
    }

    sealed class SignatureState(
        CSharpType declaringType,
        CSharpType returnType,
        ImmutableArray<MethodParameter> parameters,
        string? unsupportedReason)
    {
        internal readonly CSharpType DeclaringType = declaringType;
        internal readonly CSharpType ReturnType = returnType;
        internal readonly ImmutableArray<MethodParameter> Parameters = parameters;
        internal readonly string? UnsupportedReason = unsupportedReason;
        internal string? CanonicalSelector;
        internal string? SignatureHash;
    }
}
