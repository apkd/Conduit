using Microsoft.CodeAnalysis;

namespace Conduit;

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
            await Assert.That(artifact.Length).IsEqualTo(bytes.LongLength);
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
    [Arguments("using Probe.Resource<int> resource = new();", 1)]
    [Arguments("_ = 0;\nusing Probe.Resource<int> resource = new();", 1)]
    [Arguments("using global::" + SnippetSourceBuilder.Namespace + ".Probe.Resource<int> resource = new();", 1)]
    [Arguments("using Probe.Resource<System.Collections.Generic.List<(int X, string Y)>> resource = new();", 1)]
    [Arguments("using Probe.Resource<int>? resource = new Probe.Resource<int>();", 1)]
    [Arguments("using IDisposable resource = new Probe.Resource<int>();", 1)]
    [Arguments("using ResourceAlias resource = new();", 1)]
    [Arguments("using Probe /* ; } */ .Resource<int>\n    /* name */ @using = new();", 1)]
    [Arguments("using Probe.Resource<int> \\u0072esource = new();", 1)]
    [Arguments("using Probe.Resource<int> resource = new($\"{nameof(Probe)}: {\"; //\"}\");", 1)]
    [Arguments("using Probe.Resource<int> first = new(), second = new();", 2)]
    [Arguments("using var resource = new Probe.Resource<int>();", 1)]
    public async Task UsingDeclarationsCompileAndDisposeAtMethodExit(string statement, int expectedDisposals)
    {
        var declarations = "using System;\n"
            + $"using ResourceAlias = {SnippetSourceBuilder.Namespace}.Probe.Resource<int>;\n"
            + "public static class Probe\n"
            + "{\n"
            + "    public static int Disposed;\n"
            + "    public struct Resource<T> : IDisposable\n"
            + "    {\n"
            + "        public Resource(string text) { }\n"
            + "        public void Dispose() => Disposed++;\n"
            + "    }\n"
            + "}";
        var parsed = ConduitCodeParser.Parse(declarations + "\n" + statement + "\n"
            + "if (Probe.Disposed != 0) throw new InvalidOperationException(\"Disposed before method exit.\");\n"
            + "return new Func<int>(() => Probe.Disposed);");

        var output = CompileUsingSnippet(parsed);
        var errors = output.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        await Assert.That(errors).IsEmpty();
        await Assert.That(output.AssemblyBytes).IsNotNull();

        var execute = System.Reflection.Assembly.Load(output.AssemblyBytes!)
            .GetType(SnippetSourceBuilder.Namespace + ".TestHost")!
            .GetMethod("Execute")!;
        var getDisposed = (Func<int>)execute.Invoke(null, null)!;
        await Assert.That(getDisposed()).IsEqualTo(expectedDisposals);
    }

    [Test]
    [Arguments("", "using System.IO.MemoryStream stream = new(); return stream.CanRead ? 1 : 0;")]
    [Arguments("using System;", "return Math.Abs(-1);")]
    [Arguments("using global::System.IO;", "using MemoryStream stream = new(); return stream.CanRead ? 1 : 0;")]
    [Arguments("using Stream = System.IO.MemoryStream;", "using Stream stream = new(); return stream.CanRead ? 1 : 0;")]
    [Arguments("using @static = System.IO.MemoryStream;", "using @static stream = new(); return stream.CanRead ? 1 : 0;")]
    [Arguments("using static System.Math;", "return Abs(-1);")]
    [Arguments("using static System.Collections.Generic.Comparer<int>;", "return Default.Compare(2, 1);")]
    [Arguments("using Pair = (int X, int Y);", "Pair pair = (1, 2); return pair.X;")]
    [Arguments("using unsafe Pointer = int*;", "unsafe { Pointer value = null; return value == null ? 1 : 0; }")]
    [Arguments("using System.IO;", "using (var stream = new MemoryStream()) { return stream.CanRead ? 1 : 0; }")]
    public async Task UsingDirectivesAndResourceStatementsCompileInTheirOwnScopes(string directive, string body)
    {
        var parsed = ConduitCodeParser.Parse(directive + "\n" + body);
        var output = CompileUsingSnippet(parsed);
        var errors = output.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        await Assert.That(errors).IsEmpty();
        await Assert.That(output.AssemblyBytes).IsNotNull();

        var execute = System.Reflection.Assembly.Load(output.AssemblyBytes!)
            .GetType(SnippetSourceBuilder.Namespace + ".TestHost")!
            .GetMethod("Execute")!;
        await Assert.That(execute.Invoke(null, null)).IsEqualTo(1);
    }

    [Test]
    public async Task UsingDeclarationDiagnosticsKeepSnippetCoordinates()
    {
        var declarations = "using System.IO;\n"
            + "class Helper { }\n"
            + "static int count;";
        var body = "\nusing /* resource */ MemoryStream\n"
            + "    stream = new();\n"
            + "return missing;";
        var source = declarations + body;
        var parsed = ConduitCodeParser.Parse(source);
        await Assert.That(parsed.Body.Text).IsEqualTo(body);

        var sourceFileName = nameof(UsingDeclarationDiagnosticsKeepSnippetCoordinates) + ".cs";
        var output = CompileUsingSnippet(parsed, sourceFileName);
        var errors = output.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        await Assert.That(output.AssemblyBytes).IsNull();
        await Assert.That(errors).Count().IsEqualTo(1);
        await Assert.That(errors[0].Id).IsEqualTo("CS0103");

        var expectedPosition = Microsoft.CodeAnalysis.Text.SourceText
            .From(source)
            .Lines.GetLinePosition(source.IndexOf("missing", StringComparison.Ordinal));
        var location = errors[0].Location.GetMappedLineSpan();
        await Assert.That(location.Path).IsEqualTo(sourceFileName);
        await Assert.That(location.StartLinePosition).IsEqualTo(expectedPosition);
    }

    [Test]
    [Arguments("using System.Text;")]
    [Arguments("class Late { }")]
    [Arguments("static int late;")]
    [Arguments("namespace N { }")]
    [Arguments("global using System;")]
    [Arguments("extern alias Other;")]
    [Arguments("#define FLAG")]
    public async Task UsingDeclarationsDoNotRelaxSnippetOrderingOrUnsupportedForms(string item)
    {
        await Assert.That(() => ConduitCodeParser.Parse(
                "using System.IO.MemoryStream stream = new();\n" + item
            ))
            .Throws<SnippetParseException>();
    }

    [Test]
    public async Task PlayerDetourSourceFilenameCanBeReusedFromMemory()
    {
        var compiler = new SnippetCompiler(null!);
        var first = await compiler.PrepareDetourArtifactAsync(
            "player:12345",
            "return 7;",
            false,
            "test-session",
            CancellationToken.None
        );
        var second = await compiler.PrepareDetourArtifactAsync(
            "player:12345",
            first.Artifact!.Value.FileName,
            false,
            "test-session",
            CancellationToken.None
        );

        await Assert.That(second.Failure).IsNull();
        await Assert.That(second.Artifact).IsEqualTo(first.Artifact);

        var afterReload = await compiler.PrepareDetourArtifactAsync(
            "player:12345",
            first.Artifact.Value.FileName,
            false,
            "next-session",
            CancellationToken.None
        );
        await Assert.That(afterReload.Failure?.Outcome).IsEqualTo(ToolOutcome.CompileError);
    }

    [Test]
    public async Task EditorDetourSourceFilenameReloadsChangedFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"conduit-script-cache-{Guid.NewGuid():N}"
        );
        var compiler = new SnippetCompiler(null!);
        try
        {
            var first = await compiler.PrepareDetourArtifactAsync(
                target: root,
                source: "return 1;",
                preserveSnippets: false,
                sessionInstanceId: "test-session",
                ct: CancellationToken.None
            );
            var firstArtifact = first.Artifact!.Value;
            var path = Path.Combine(root, "Temp", "Conduit", firstArtifact.FileName);
            var writeTime = File.GetLastWriteTimeUtc(path);

            // preserve size and timestamp to prove the source text invalidates the cache.
            await File.WriteAllTextAsync(path, "return 2;");
            File.SetLastWriteTimeUtc(path, writeTime);

            var changed = await compiler.PrepareDetourArtifactAsync(
                target: root,
                source: firstArtifact.FileName,
                preserveSnippets: false,
                sessionInstanceId: "test-session",
                ct: CancellationToken.None
            );
            var unchanged = await compiler.PrepareDetourArtifactAsync(
                target: root,
                source: firstArtifact.FileName,
                preserveSnippets: false,
                sessionInstanceId: "test-session",
                ct: CancellationToken.None
            );

            await Assert.That(changed.Artifact?.Source).IsEqualTo("return 2;");
            await Assert.That(changed.Artifact?.Id).IsNotEqualTo(firstArtifact.Id);
            await Assert.That(unchanged.Artifact).IsEqualTo(changed.Artifact);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ExecuteCodeCompilationSuppressesNullableWarnings()
    {
        var parsed = ConduitCodeParser.Parse(
            "string? value = null;\n"
            + "return value.Length;"
        );
        var output = SnippetCompilationEngine.Compile(
            "TestHost",
            "test.cs",
            parsed,
            platformReferences,
            ["using System;"],
            [],
            async: false,
            returnsValue: true
        );
        var warnings = output.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
            .ToArray();

        await Assert.That(output.AssemblyBytes).IsNotNull();
        await Assert.That(warnings).IsEmpty();
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

        var recovered = SnippetReturnNormalizer.TryNormalizeBareReturns(
            parsed.Body,
            objectResult.Diagnostics,
            noResult.Diagnostics,
            out var normalized
        );

        await Assert.That(recovered).IsTrue();
        await Assert.That(normalized.Text).Contains("int Broken() { return; }");
        await Assert.That(normalized.Text).Contains("if (DateTime.UtcNow.Ticks > 0) return null;");

        SnippetCompilationEngine.Output Compile(SnippetParseResult snippet, bool returnsValue) =>
            SnippetCompilationEngine.Compile(
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

    static SnippetCompilationEngine.Output CompileUsingSnippet(SnippetParseResult parsed, string sourceFileName = "test.cs") =>
        SnippetCompilationEngine.Compile(
            "TestHost",
            sourceFileName,
            parsed,
            platformReferences,
            [],
            [],
            async: false,
            returnsValue: true
        );
}
