using Microsoft.CodeAnalysis;
using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SnippetCompilerTests
{
    static readonly MetadataReference[] platformReferences =
        ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
         ?? throw new InvalidOperationException("Trusted platform assemblies were not provided."))
        .Split(Path.PathSeparator)
        .Select(static path => MetadataReference.CreateFromFile(path))
        .ToArray();

    [Test]
    public async Task AccessiblePlayerReferenceUsesItsExactPath()
    {
        var path = typeof(SnippetCompilerTests).Assembly.Location;
        var reference = new BridgeAssemblyReference
        {
            Id = "the runtime identity is only needed when transferring the file",
            Path = path,
            Length = new FileInfo(path).Length,
        };

        await Assert.That(SnippetCompiler.TryResolveAccessiblePath(reference))
            .IsEqualTo(path);
    }

    [Test]
    public async Task AccessiblePlayerReferenceRejectsDifferentFileLength()
    {
        var path = typeof(SnippetCompilerTests).Assembly.Location;
        var reference = new BridgeAssemblyReference
        {
            Path = path,
            Length = new FileInfo(path).Length + 1,
        };

        await Assert.That(SnippetCompiler.TryResolveAccessiblePath(reference)).IsNull();
    }

    [Test]
    public async Task ProtonZDriveReferenceMapsToItsLinuxHostPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = typeof(SnippetCompilerTests).Assembly.Location;
        var reference = new BridgeAssemblyReference
        {
            Path = "Z:" + path.Replace('/', '\\'),
            Length = new FileInfo(path).Length,
        };

        await Assert.That(SnippetCompiler.TryResolveAccessiblePath(reference))
            .IsEqualTo(path);
    }

    [Test]
    [Arguments(false, "Temp/Conduit")]
    [Arguments(true, "Library/Conduit")]
    public async Task EditorArtifactUsesConfiguredProjectRelativeFile(
        bool preserveSnippets,
        string storageDirectory
    )
    {
        var root = Path.Combine(Path.GetTempPath(), $"conduit-artifact-{Guid.NewGuid():N}");
        var bytes = "compiled"u8.ToArray();
        try
        {
            var artifact = await SnippetCompiler.CreateArtifactAsync(
                root,
                "1.dll",
                "application/vnd.microsoft.portable-executable",
                preserveSnippets,
                bytes,
                CancellationToken.None
            );

            await Assert.That(artifact.RelativePath?.Replace('\\', '/'))
                .IsEqualTo($"{storageDirectory}/1.dll");
            await Assert.That(artifact.Chunks).Count().IsEqualTo(0);
            await Assert.That(File.ReadAllBytes(Path.Combine(root, storageDirectory, "1.dll")))
                .IsEquivalentTo(bytes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ParserSeparatesImportsDeclarationsFieldsAndBody()
    {
        var parsed = ConduitCodeParser.Parse(
            "using System;\n"
            + "sealed class Helper { }\n"
            + "static int count;\n"
            + "count++; return count;"
        );

        await Assert.That(parsed.Usings).Count().IsEqualTo(1);
        await Assert.That(parsed.TypeDeclarations).Count().IsEqualTo(1);
        await Assert.That(parsed.StaticFields).Count().IsEqualTo(1);
        await Assert.That(parsed.Body.Text).Contains("count++; return count;");
    }

    [Test]
    public async Task ParserRejectsNamespaceAndPreprocessorDirectives()
    {
        await Assert.That(() => ConduitCodeParser.Parse("namespace Invalid { }"))
            .Throws<SnippetParseException>();
        await Assert.That(() => ConduitCodeParser.Parse("#define INVALID"))
            .Throws<SnippetParseException>();
    }

    [Test]
    public async Task ParserAcceptsSearchHelperInsideInterpolation()
    {
        var parsed = ConduitCodeParser.Parse(
            "return $\"{SearchMany<GameObject>(\"ConduitDirtySave\").Length}\";"
        );

        await Assert.That(parsed.Body.Text)
            .IsEqualTo("return $\"{SearchMany<GameObject>(\"ConduitDirtySave\").Length}\";");
    }

    [Test]
    public async Task PlayerDetourSourceFilenameCanBeReusedFromMemory()
    {
        var compiler = new SnippetCompiler(null!);
        var first = await compiler.PrepareDetourArtifactAsync(
            "player:12345",
            "return 7;",
            false,
            CancellationToken.None
        );
        var second = await compiler.PrepareDetourArtifactAsync(
            "player:12345",
            first.Artifact!.Value.FileName,
            false,
            CancellationToken.None
        );

        await Assert.That(second.Failure).IsNull();
        await Assert.That(second.Artifact).IsEqualTo(first.Artifact);
    }

    [Test]
    public async Task BareReturnRecoveryRewritesOnlyTheGeneratedEntryPointBody()
    {
        var parsed = ConduitCodeParser.Parse(
            "int Broken() { return; }\n"
            + "if (DateTime.UtcNow.Ticks > 0) return;\n"
            + "return 1;"
        );
        var objectResult = Compile(parsed, returnsValue: true);
        var noResult = Compile(parsed, returnsValue: false);

        var recovered = SnippetCompiler.TryNormalizeBareReturns(
            parsed.Body,
            objectResult.Diagnostics,
            noResult.Diagnostics,
            out var normalized
        );

        await Assert.That(recovered).IsTrue();
        await Assert.That(normalized.Text).Contains("int Broken() { return; }");
        await Assert.That(normalized.Text).Contains("if (DateTime.UtcNow.Ticks > 0) return null;");

        SnippetCompiler.CompilationOutput Compile(SnippetParseResult snippet, bool returnsValue) =>
            SnippetCompiler.Compile(
                "TestHost",
                "test.cs",
                snippet,
                platformReferences,
                ["using System;"],
                [],
                async: false,
                returnsValue
            );
    }
}
