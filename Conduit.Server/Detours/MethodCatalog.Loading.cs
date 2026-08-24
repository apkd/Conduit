using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;

namespace Conduit;

sealed partial class MethodCatalog
{
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
}

