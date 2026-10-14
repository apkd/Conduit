using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

public sealed class SignatureSnapshotOptimizationTests
{
    [Test]
    public async Task DecodedSignatureHashMatchesTheRawMetadataBlob()
    {
        var path = typeof(SignatureSnapshotOptimizationTests).Assembly.Location;
        var method = typeof(SignatureSnapshotOptimizationTests).GetMethod(nameof(SignatureFixture))!;
        var decoded = MethodSignatureDecoder.DecodeMethodSignature(path, method.MetadataToken);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var definition = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ff_ffff));
        var expected = Convert.ToHexStringLower(SHA256.HashData(reader.GetBlobBytes(definition.Signature)));
        await Assert.That(decoded.SignatureHash.Value).IsEqualTo(expected);
        await Assert.That(decoded.Parameters.Length).IsEqualTo(1);
    }

    [Test]
    public async Task ReadingHashAfterSignatureDecodeDoesNotReopenTheAssembly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conduit-signature-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fixture.dll");
            var compilation = CSharpCompilation.Create(
                "SignatureOptimizationFixture",
                [CSharpSyntaxTree.ParseText("public static class SignatureOwner { public static int Value(int value) => value; }")],
                [CompilationMetadata.CreateReference(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            using (var output = File.Create(path))
            {
                var emitted = compilation.Emit(output);
                if (!emitted.Success)
                    throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
            }
            var catalog = MethodCatalog.Create([path]);
            var target = catalog.Resolve("SignatureOwner.Value").Target
                         ?? throw new InvalidOperationException("The signature fixture was not resolved.");
            // force decoding but deliberately do not access SignatureHash before deleting the file.
            var declaration = target.ReplacementDeclaration;
            File.Delete(path);

            await Assert.That(target.SignatureHash.Length).IsEqualTo(64);
            await Assert.That(target.ReplacementDeclaration).IsEqualTo(declaration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static int SignatureFixture(int value) => value;
}
