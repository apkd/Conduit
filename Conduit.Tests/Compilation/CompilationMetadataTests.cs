using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

public sealed class CompilationMetadataTests
{
    [Test]
    public async Task ReferencesKeepTheirSnapshotAfterAssemblyReplacement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conduit-metadata-{Guid.NewGuid():N}.dll");
        var core = CompilationMetadata.CreateReference(typeof(object).Assembly.Location);
        try
        {
            File.WriteAllBytes(path, Emit("public static class Fixture { public static int Before() => 1; }", core));
            var before = CompilationMetadata.CreateReference(path);

            // unity replaces assemblies on reload; cached metadata must neither lock nor follow the file.
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                stream.Write(Emit("public static class Fixture { public static int After() => 2; }", core));
            var after = CompilationMetadata.CreateReference(path);
            File.Delete(path);

            await Assert.That(Emit("public class Consumer { public int Read() => Fixture.Before(); }", core, before))
                .IsNotEmpty();
            await Assert.That(Emit("public class Consumer { public int Read() => Fixture.After(); }", core, after))
                .IsNotEmpty();
        }
        finally
        {
            File.Delete(path);
        }

        static byte[] Emit(string source, params MetadataReference[] references)
        {
            var compilation = CSharpCompilation.Create(
                Guid.NewGuid().ToString("N"),
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new(OutputKind.DynamicallyLinkedLibrary)
            );
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            if (!result.Success)
                throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
            return stream.ToArray();
        }
    }
}
