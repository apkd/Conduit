using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

sealed class MethodCatalog
{
    readonly MethodTarget[] indexedMethods;
    readonly Dictionary<string, MethodBucket> methodBuckets;
    readonly ConcurrentDictionary<string, MethodResolution> resolutionCache = new(StringComparer.Ordinal);

    MethodCatalog(MethodTarget[][] methodSets, int methodCount)
    {
        // real Unity metadata averages about two unique lookup names per five method rows.
        methodBuckets = new(methodCount * 2 / 5, StringComparer.Ordinal);
        foreach (var methods in methodSets)
            foreach (var method in methods)
            {
                Count(method.MethodName);
                var separator = method.MethodName.LastIndexOf('.');

                // explicit-interface short names repeat heavily; span lookup avoids one substring per method.
                if (separator >= 0)
                    CountShort(method.MethodName.AsSpan(separator + 1));
            }

        var indexedCount = 0;
        foreach (var name in methodBuckets.Keys)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(methodBuckets, name);
            var count = bucket.Count;
            bucket = new(indexedCount, 0);
            indexedCount += count;
        }

        indexedMethods = new MethodTarget[indexedCount];
        foreach (var methods in methodSets)
            foreach (var method in methods)
            {
                Index(method.MethodName, method);
                var separator = method.MethodName.LastIndexOf('.');
                if (separator >= 0)
                    IndexShort(method.MethodName.AsSpan(separator + 1), method);
            }

        void Count(string name)
            => CollectionsMarshal.GetValueRefOrAddDefault(methodBuckets, name, out _).Count++;

        void CountShort(ReadOnlySpan<char> name)
        {
            var lookup = methodBuckets.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(name, out var bucket))
                lookup[name] = new(bucket.Offset, bucket.Count + 1);
            else
                methodBuckets.Add(name.ToString(), new(0, 1));
        }

        void Index(string name, MethodTarget method)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(methodBuckets, name);
            indexedMethods[bucket.Offset + bucket.Count++] = method;
        }

        void IndexShort(ReadOnlySpan<char> name, MethodTarget method)
        {
            var lookup = methodBuckets.GetAlternateLookup<ReadOnlySpan<char>>();
            var bucket = lookup[name];
            indexedMethods[bucket.Offset + bucket.Count++] = method;
            lookup[name] = bucket;
        }
    }

    internal static MethodCatalog Create(IEnumerable<string> referencePaths)
    {
        var paths = referencePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var methodSets = new MethodTarget[paths.Length][];
        if (paths.Length == 1)
            methodSets[0] = ReadAssembly(paths[0]);
        else if (paths.Length > 1)
        {
            var errors = new ExceptionDispatchInfo?[paths.Length];
            Parallel.For(0, paths.Length, index =>
            {
                try
                {
                    methodSets[index] = ReadAssembly(paths[index]);
                }
                catch (Exception exception)
                {
                    errors[index] = ExceptionDispatchInfo.Capture(exception);
                }
            });

            foreach (var error in errors)
                error?.Throw();
        }

        var methodCount = 0;
        foreach (var methodSet in methodSets)
            methodCount += methodSet.Length;

        return new(methodSets, methodCount);

        static MethodTarget[] ReadAssembly(string path)
        {
            MethodTarget[] methods = [];
            var methodCount = 0;
            try
            {
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    return [];

                var reader = pe.GetMetadataReader();
                if (!reader.IsAssembly)
                    return [];

                methods = new MethodTarget[reader.MethodDefinitions.Count];
                var assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
                var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
                var assembly = new MethodAssemblyInfo(mvid, assemblyName, path);
                var provider = new CSharpSignatureProvider(reader);
                var declaringTypes = new Dictionary<TypeDefinitionHandle, DeclaringTypeInfo>(reader.TypeDefinitions.Count);
                // method names are shared heavily; half the row count closely matches real metadata.
                var methodNames = new Dictionary<StringHandle, string>((reader.MethodDefinitions.Count + 1) / 2);
                foreach (var handle in reader.MethodDefinitions)
                {
                    var definition = reader.GetMethodDefinition(handle);
                    var declaringHandle = definition.GetDeclaringType();
                    if (!declaringTypes.TryGetValue(declaringHandle, out var declaring))
                    {
                        var declaringDefinition = reader.GetTypeDefinition(declaringHandle);
                        declaring = new(
                            assembly,
                            declaringDefinition.GetGenericParameters().Count > 0,
                            provider.GetMetadataTypeName(declaringHandle)
                        );
                        declaringTypes.Add(declaringHandle, declaring);
                    }

                    if (!methodNames.TryGetValue(definition.Name, out var name))
                    {
                        name = reader.GetString(definition.Name);
                        methodNames.Add(definition.Name, name);
                    }
                    var unsupported = GetMetadataUnsupportedReason(
                        reader,
                        definition,
                        declaring.IsGeneric,
                        name
                    );

                    methods[methodCount++] = new(
                        declaring,
                        MetadataTokens.GetToken(handle),
                        name,
                        (definition.Attributes & MethodAttributes.Static) != 0,
                        unsupported
                    );
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                // unity can report native images and transiently unavailable files alongside managed references.
            }

            if (methodCount != methods.Length)
                Array.Resize(ref methods, methodCount);
            return methods;
        }
    }

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

    internal MethodResolution Resolve(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return MethodResolution.Failed("`methodName` must identify a method.");

        if (resolutionCache.TryGetValue(selector, out var cached))
            return cached;

        var resolved = ResolveUncached(selector);
        return resolved.Target == null
            ? resolved
            : resolutionCache.GetOrAdd(selector, resolved);
    }

    MethodResolution ResolveUncached(string selector)
    {
        if (!TryGetMethodNameRange(selector, out var methodNameStart, out var methodNameLength))
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");
        var lookup = methodBuckets.GetAlternateLookup<ReadOnlySpan<char>>();
        var methodName = selector.AsSpan(methodNameStart, methodNameLength);
        if (!lookup.TryGetValue(methodName, out var bucket)
            && (methodName[0] != '@' || !lookup.TryGetValue(methodName[1..], out bucket)))
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");

        List<MethodTarget>? matches = null;
        List<MethodTarget>? supported = null;
        var end = bucket.Offset + bucket.Count;
        for (var index = bucket.Offset; index < end; index++)
        {
            var method = indexedMethods[index];
            if (!method.Matches(selector))
                continue;

            (matches ??= []).Add(method);
            if (method.UnsupportedReason is null)
                (supported ??= []).Add(method);
        }

        if (matches is not { Count: > 0 })
            return MethodResolution.Failed($"No loaded managed method matches '{selector}'.");

        if (supported is { Count: 1 })
            return MethodResolution.Succeeded(supported[0]);

        if (supported is not { Count: > 0 })
        {
            var reasons = matches
                .Select(method => $"{method.CanonicalSelector}: {method.UnsupportedReason}")
                .Distinct(StringComparer.Ordinal);
            return MethodResolution.Failed(string.Join("\n", reasons));
        }

        return MethodResolution.Ambiguous(
            "Method selector is ambiguous. Use one of:\n" + string.Join("\n", supported.Select(static method => method.CanonicalSelector))
        );

        static bool TryGetMethodNameRange(
            string selector,
            out int start,
            out int length)
        {
            var parameterStart = selector.IndexOf('(');
            var end = parameterStart < 0 ? selector.Length : parameterStart;
            if (end == 0)
            {
                start = 0;
                length = 0;
                return false;
            }

            var separator = selector.LastIndexOf('.', end - 1);
            if (separator < 0 || separator + 1 == end)
            {
                start = 0;
                length = 0;
                return false;
            }

            start = separator + 1;
            length = end - start;
            return true;
        }
    }

    struct MethodBucket(int offset, int count)
    {
        internal int Offset = offset;
        internal int Count = count;
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

    // one shared identity per declaring type keeps the much larger method table compact.
    internal sealed class DeclaringTypeInfo(
        MethodAssemblyInfo assembly,
        bool isGeneric,
        string name)
    {
        internal MethodAssemblyInfo Assembly { get; } = assembly;
        internal bool IsGeneric { get; } = isGeneric;
        internal string Name { get; } = name;
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

    static MetadataUnsupportedReason GetMetadataUnsupportedReason(
        MetadataReader reader,
        MethodDefinition method,
        bool declaringTypeIsGeneric,
        string name)
    {
        if (name is ".ctor" or ".cctor")
            return MetadataUnsupportedReason.Constructor;
        if (method.GetGenericParameters().Count > 0 || declaringTypeIsGeneric)
            return MetadataUnsupportedReason.Generic;
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            return MetadataUnsupportedReason.PInvoke;
        if ((method.ImplAttributes & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native)) != 0)
            return MetadataUnsupportedReason.Runtime;
        if ((method.Attributes & MethodAttributes.Abstract) != 0 || method.RelativeVirtualAddress == 0)
            return MetadataUnsupportedReason.NoBody;
        if (reader.GetBlobReader(method.Signature).ReadSignatureHeader().CallingConvention
            == SignatureCallingConvention.VarArgs)
            return MetadataUnsupportedReason.VarArgs;

        return MetadataUnsupportedReason.None;
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
                MethodCatalog.ReadMethodSignatureBlob(AssemblyPath, MetadataToken)
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

            return string.Equals(selector, CanonicalSelector, StringComparison.Ordinal);
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

            var value = MethodCatalog.DecodeMethodSignature(AssemblyPath, MetadataToken);
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

sealed class MethodAssemblyInfo(Guid moduleVersionId, string name, string path)
{
    internal Guid ModuleVersionId { get; } = moduleVersionId;
    internal string Name { get; } = name;
    internal string Path { get; } = path;
}

enum MetadataUnsupportedReason : byte
{
    None,
    Constructor,
    Generic,
    PInvoke,
    Runtime,
    NoBody,
    VarArgs,
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
