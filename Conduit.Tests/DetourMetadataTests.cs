using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

public sealed class DetourMetadataTests
{
    [Test]
    public async Task CatalogDescribesRequiredAbiShapes()
    {
        var catalog = MethodCatalog.Create([typeof(DetourMetadataTests).Assembly.Location]);

        var refReadonly = catalog.Resolve("DetourSignatureFixture.RefReadonly").Target;
        await Assert.That(refReadonly).IsNotNull();
        await Assert.That(refReadonly!.ReplacementDeclaration).Contains("ref readonly int Replace(int arg0)");

        var span = catalog.Resolve("DetourSignatureFixture.EchoSpan").Target;
        await Assert.That(span).IsNotNull();
        await Assert.That(span!.ReplacementDeclaration).Contains(
            "global::System.Span<int> Replace(global::System.Span<int> arg0)"
        );

        var pointer = catalog.Resolve("DetourSignatureFixture.EchoPointer").Target;
        await Assert.That(pointer).IsNotNull();
        await Assert.That(pointer!.ReplacementDeclaration).Contains("int* Replace(int* arg0)");

        var functionPointer = catalog.Resolve("DetourSignatureFixture.EchoFunctionPointer").Target;
        await Assert.That(functionPointer).IsNotNull();
        await Assert.That(functionPointer!.ReplacementDeclaration).Contains("delegate* unmanaged[Cdecl]<int, int>");
    }

    [Test]
    public async Task CatalogEscapesKeywordTypeAndMethodIdentifiers()
    {
        var catalog = MethodCatalog.Create([typeof(DetourMetadataTests).Assembly.Location]);

        var method = catalog.Resolve("DetourSignatureFixture.@event").Target;

        await Assert.That(method).IsNotNull();
        await Assert.That(method!.ReplacementDeclaration).Contains(
            "global::Conduit.DetourMetadataTests.@class Replace("
            + "global::Conduit.DetourMetadataTests.@class arg0)"
        );
    }

    [Test]
    public async Task CatalogRejectsConstructorsAndGenericDeclarations()
    {
        var catalog = MethodCatalog.Create([typeof(DetourMetadataTests).Assembly.Location]);

        var constructor = catalog.Resolve("DetourSignatureFixture..ctor");
        await Assert.That(constructor.Target).IsNull();
        await Assert.That(constructor.Diagnostic).Contains("constructors are not supported");

        var generic = catalog.Resolve("GenericDetourFixture.Echo");
        await Assert.That(generic.Target).IsNull();
        await Assert.That(generic.Diagnostic).Contains("generic methods and methods declared on generic types are not supported");
    }

    [Test]
    public async Task CatalogDescribesParameterModifiersAndCanonicalOverloads()
    {
        var catalog = MethodCatalog.Create([typeof(DetourMetadataTests).Assembly.Location]);

        var modifiers = catalog.Resolve("DetourSignatureFixture.RefParameters").Target;
        await Assert.That(modifiers).IsNotNull();
        await Assert.That(modifiers!.ReplacementDeclaration)
            .Contains("void Replace(ref int arg0, in int arg1, out int arg2)");

        var refReadonly = catalog.Resolve("DetourSignatureFixture.RefReadonlyParameter").Target;
        await Assert.That(refReadonly).IsNotNull();
        await Assert.That(refReadonly!.ReplacementDeclaration)
            .Contains("void Replace(ref readonly int arg0)");

        var ambiguous = catalog.Resolve("DetourSignatureFixture.Overload");
        await Assert.That(ambiguous.Target).IsNull();
        await Assert.That(ambiguous.Outcome).IsEqualTo(ToolOutcome.AmbiguousTarget);
        await Assert.That(ambiguous.Diagnostic).Contains("(int)->int");
        await Assert.That(ambiguous.Diagnostic).Contains("(string)->string");

        var canonical = ambiguous.Diagnostic!
            .Split('\n')
            .Single(static line => line.EndsWith("(int)->int", StringComparison.Ordinal));
        await Assert.That(catalog.Resolve(canonical).Target).IsNotNull();
    }

    [Test]
    public async Task CatalogRejectsAbstractAndNativeMethods()
    {
        var catalog = MethodCatalog.Create([typeof(DetourMetadataTests).Assembly.Location]);

        var abstractMethod = catalog.Resolve("AbstractDetourFixture.Abstract");
        await Assert.That(abstractMethod.Target).IsNull();
        await Assert.That(abstractMethod.Diagnostic).Contains("no managed implementation body");

        var nativeMethod = catalog.Resolve("DetourSignatureFixture.Native");
        await Assert.That(nativeMethod.Target).IsNull();
        await Assert.That(nativeMethod.Diagnostic).Contains("P/Invoke methods are not supported");
    }

    [Test]
    public async Task PublicizerChangesVisibilityWithoutChangingModuleIdentity()
    {
        var path = typeof(DetourMetadataTests).Assembly.Location;
        var originalMvid = ReadMvid(File.ReadAllBytes(path));
        var bytes = MetadataPublicizer.Publicize(path);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var fixture = reader.TypeDefinitions
            .Select(reader.GetTypeDefinition)
            .Single(definition => reader.GetString(definition.Name) == nameof(DetourSignatureFixture));

        await Assert.That(fixture.Attributes & TypeAttributes.VisibilityMask).IsEqualTo(TypeAttributes.NestedPublic);
        foreach (var methodHandle in fixture.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            await Assert.That(method.Attributes & MethodAttributes.MemberAccessMask).IsEqualTo(MethodAttributes.Public);
        }
        foreach (var fieldHandle in fixture.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            await Assert.That(field.Attributes & FieldAttributes.FieldAccessMask).IsEqualTo(FieldAttributes.Public);
        }
        await Assert.That(ReadMvid(bytes)).IsEqualTo(originalMvid);

        static Guid ReadMvid(byte[] image)
        {
            using var stream = new MemoryStream(image, writable: false);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            return reader.GetGuid(reader.GetModuleDefinition().Mvid);
        }
    }

    [Test]
    public async Task GeneratedReplacementDeclarationsCompileAgainstPublicizedMetadata()
    {
        var path = typeof(DetourMetadataTests).Assembly.Location;
        var catalog = MethodCatalog.Create([path]);
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                          ?? throw new InvalidOperationException("Trusted platform assemblies were not provided."))
            .Split(Path.PathSeparator)
            .Select(static referencePath => MetadataReference.CreateFromFile(referencePath))
            .ToList();
        references.Add(
            MetadataReference.CreateFromImage(
                ImmutableArray.Create(MetadataPublicizer.Publicize(path)),
                filePath: path
            )
        );

        foreach (var methodName in new[]
                 {
                     "RefReadonly",
                     "EchoSpan",
                     "EchoPointer",
                     "EchoFunctionPointer",
                     "Instance",
                 })
        {
            var target = catalog.Resolve("DetourSignatureFixture." + methodName).Target
                         ?? throw new InvalidOperationException(methodName);
            var source = DetourSourceBuilder.BuildSource(
                target,
                "Generated_" + methodName,
                methodName + ".cs",
                ConduitCodeParser.Parse("throw null;"),
                [],
                [],
                async: false
            );
            var compilation = CSharpCompilation.Create(
                "Generated_" + methodName,
                [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), encoding: Encoding.UTF8)],
                references,
                new(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true,
                    nullableContextOptions: NullableContextOptions.Enable
                )
            );
            var errors = compilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            await Assert.That(errors).IsEmpty();
        }
    }

    [Test]
    public async Task ProbeBodiesReturnWithoutThrowingWhenTheSignatureAllowsIt()
    {
        await Assert.That(DetourSourceBuilder.BuildProbeBody(new("void", "void")))
            .IsEqualTo("return;");
        await Assert.That(DetourSourceBuilder.BuildProbeBody(new("int", "int")))
            .IsEqualTo("return default;");
        await Assert.That(DetourSourceBuilder.BuildProbeBody(new("int", "int", IsByRef: true)))
            .Contains("NotSupportedException");
    }

    unsafe sealed class DetourSignatureFixture
    {
        static int value;

        public static ref readonly int RefReadonly(int arg) => ref value;
        public static Span<int> EchoSpan(Span<int> arg) => arg;
        public static int* EchoPointer(int* arg) => arg;
        public static delegate* unmanaged[Cdecl]<int, int> EchoFunctionPointer(
            delegate* unmanaged[Cdecl]<int, int> arg) => arg;
        public static @class @event(@class value) => value;
        public static void RefParameters(ref int byRef, in int byIn, out int byOut) =>
            byOut = byRef + byIn;
        public static void RefReadonlyParameter(ref readonly int location) { }
        public static int Overload(int value) => value;
        public static string Overload(string value) => value;
        [System.Runtime.InteropServices.DllImport("conduit-detour-test")]
        public static extern int Native();
        public int Instance(int arg) => arg;
    }

    abstract class AbstractDetourFixture
    {
        public abstract int Abstract();
    }

    sealed class GenericDetourFixture<T>
    {
        public static T Echo(T value) => value;
    }

    sealed class @class { }
}

static class DetourAccessProbe
{
    static int Value => 1;
}
