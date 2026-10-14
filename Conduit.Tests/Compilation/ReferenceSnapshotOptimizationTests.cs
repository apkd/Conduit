using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

public sealed class ReferenceSnapshotOptimizationTests
{
    [Test]
    public async Task SamePathAndModuleReuseTheSameReference()
    {
        using var files = new TestAssemblies();
        var path = files.Emit("shared.dll", "namespace Shared { public class SharedType {} }");
        var descriptor = Describe(path);
        var first = Build("first", descriptor, path);
        var second = Build("second", descriptor, path, first);

        await Assert.That(ReferenceEquals(first.References[0], second.References[0])).IsTrue();
        await Assert.That(ReferenceEquals(first.GetDetourCache(), second.GetDetourCache())).IsFalse();
        await Assert.That(ReferenceEquals(first.GetDetourCache(), first.GetDetourCache())).IsTrue();
        await Assert.That(second.Paths[0]).IsEqualTo(path);
    }

    [Test]
    public async Task RebuiltModuleAtSamePathIsNotShared()
    {
        using var files = new TestAssemblies();
        var path = files.Emit("versioned.dll", "public class Versioned { public int Get() => 1; }");
        var original = Describe(path);
        var first = Build("first", original, path);
        files.Emit("versioned.dll", "public class Versioned { public int Get() => 2; }");
        var changed = Describe(path);
        var second = Build("second", changed, path, first);

        await Assert.That(original.Id != changed.Id).IsTrue();
        await Assert.That(original.Length).IsEqualTo(changed.Length);
        await Assert.That(ReferenceEquals(first.References[0], second.References[0])).IsFalse();
    }

    [Test]
    public async Task DifferentPathsAreNotSharedEvenForIdenticalBytes()
    {
        using var files = new TestAssemblies();
        var firstPath = files.Emit("one.dll", "public class CopiedType {}");
        var secondPath = Path.Combine(files.DirectoryPath, "two.dll");
        File.Copy(firstPath, secondPath);
        var first = Build("first", Describe(firstPath), firstPath);
        var second = Build("second", Describe(secondPath), secondPath, first);

        await Assert.That(ReferenceEquals(first.References[0], second.References[0])).IsFalse();
    }

    [Test]
    [Arguments("opaque")]
    [Arguments("00000000000000000000000000000000")]
    [Arguments("11111111111111111111111111111111")]
    public async Task UnverifiedIdentitiesAreNeverShared(string id)
    {
        using var files = new TestAssemblies();
        var path = files.Emit("identity.dll", "public class IdentityType {}");
        var descriptor = Describe(path);
        descriptor.Id = id;
        var first = Build("first", descriptor, path);
        var second = Build("second", descriptor, path, first);

        await Assert.That(first.References.Length).IsEqualTo(1);
        await Assert.That(second.References.Length).IsEqualTo(1);
        await Assert.That(ReferenceEquals(first.References[0], second.References[0])).IsFalse();
    }

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(1L)]
    public async Task UnknownOrMismatchedLengthDoesNotCreateASharingEntry(long length)
    {
        using var files = new TestAssemblies();
        var path = files.Emit("length.dll", "public class LengthType {}");
        var descriptor = Describe(path);
        descriptor.Length = length;
        var first = Build("first", descriptor, path);
        var second = Build("second", descriptor, path, first);
        await Assert.That(ReferenceEquals(first.References[0], second.References[0])).IsFalse();
    }

    [Test]
    public async Task NonManagedImageIsSkipped()
    {
        using var files = new TestAssemblies();
        var path = Path.Combine(files.DirectoryPath, "native.dll");
        File.WriteAllText(path, "not a managed PE image");
        var descriptor = new BridgeAssemblyReference { Id = "native", Path = path, Length = new FileInfo(path).Length };
        var set = Build("first", descriptor, path);
        await Assert.That(set.References.Length).IsEqualTo(0);
        await Assert.That(set.Paths.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InferenceUsesSnapshotAfterBackingFileIsDeleted()
    {
        using var files = new TestAssemblies();
        var path = files.Emit("snapshot.dll", "namespace FrozenNamespace { public class FrozenType {} }");
        var reference = CompilationMetadata.CreateReference(path);
        File.Delete(path);

        var namespaces = SnippetNamespaceInference.InferNamespaces([Missing("FrozenType")], [reference], []);

        await Assert.That(namespaces.SequenceEqual(["FrozenNamespace"])).IsTrue();
    }

    [Test]
    public async Task InferenceIgnoresReplacementFileAndKeepsOriginalMetadata()
    {
        using var files = new TestAssemblies();
        var path = files.Emit("snapshot.dll", "namespace Original { public class StableType {} }");
        var reference = CompilationMetadata.CreateReference(path);
        files.Emit("snapshot.dll", "namespace Replaced { public class StableType {} }");

        var namespaces = SnippetNamespaceInference.InferNamespaces([Missing("StableType")], [reference], []);

        await Assert.That(namespaces.SequenceEqual(["Original"])).IsTrue();
    }

    [Test]
    public async Task InferencePreservesAmbiguityAndExistingNamespaceFiltering()
    {
        using var files = new TestAssemblies();
        var path = files.Emit("ambiguous.dll", "namespace A { public class Duplicate {} public class Unique {} } namespace B { public class Duplicate {} }");
        var reference = CompilationMetadata.CreateReference(path);
        var ambiguous = SnippetNamespaceInference.InferNamespaces([Missing("Duplicate")], [reference], []);
        var alreadyPresent = SnippetNamespaceInference.InferNamespaces([Missing("Unique")], [reference], ["A"]);
        await Assert.That(ambiguous.Count).IsEqualTo(0);
        await Assert.That(alreadyPresent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InferenceLargeErrorSetUsesTheSameMatchingRules()
    {
        using var files = new TestAssemblies();
        var names = Enumerable.Range(0, 8).Select(static index => "Type" + index).ToArray();
        var source = "namespace Many { " + string.Join(" ", names.Select(static name => "public class " + name + " {}")) + " }";
        var path = files.Emit("many.dll", source);
        var reference = CompilationMetadata.CreateReference(path);
        File.Delete(path);
        var cache = new ConcurrentDictionary<string, string[]>();
        var namespaces = SnippetNamespaceInference.InferNamespaces(names.Select(Missing), [reference], [], cache);
        await Assert.That(namespaces.SequenceEqual(["Many"])).IsTrue();
        await Assert.That(cache.Count).IsEqualTo(8);
    }

    [Test]
    public async Task CachedInferenceNeedsNoReferenceScan()
    {
        var cache = new ConcurrentDictionary<string, string[]>();
        cache["CachedType"] = ["CachedNamespace"];
        var namespaces = SnippetNamespaceInference.InferNamespaces([Missing("CachedType")], [], [], cache);
        await Assert.That(namespaces.SequenceEqual(["CachedNamespace"])).IsTrue();
    }

    static Diagnostic Missing(string name) => Diagnostic.Create(
        new DiagnosticDescriptor("CS0246", "Missing type", "The type or namespace name '{0}' could not be found", "Compiler", DiagnosticSeverity.Error, true),
        Location.None,
        name
    );

    static CompilationReferenceSet Build(string session, BridgeAssemblyReference descriptor, string path, params CompilationReferenceSet[] existing)
        => CompilationReferenceSet.Create(
            session,
            [descriptor],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [descriptor.Id] = path },
            existing
        );

    static BridgeAssemblyReference Describe(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return new()
        {
            Id = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("N"),
            AssemblyName = reader.GetString(reader.GetAssemblyDefinition().Name),
            Path = path,
            Length = stream.Length,
        };
    }

    sealed class TestAssemblies : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "conduit-reference-tests-" + Guid.NewGuid().ToString("N"));

        internal TestAssemblies() => Directory.CreateDirectory(DirectoryPath);

        internal string Emit(string name, string source)
        {
            var path = Path.Combine(DirectoryPath, name);
            var compilation = CSharpCompilation.Create(
                Path.GetFileNameWithoutExtension(name),
                [CSharpSyntaxTree.ParseText(source)],
                [CompilationMetadata.CreateReference(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release)
            );
            using var output = File.Create(path);
            var result = compilation.Emit(output);
            if (!result.Success)
                throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
            return path;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
