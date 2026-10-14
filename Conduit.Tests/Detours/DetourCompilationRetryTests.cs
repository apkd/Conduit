using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit;

public sealed class DetourCompilationRetryTests
{
    static readonly MetadataReference core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    [Test]
    public async Task SyntaxErrorsDoNotRequestFullPublicization()
    {
        var compilation = Compile("public class C { public int M() { return 1 } }");
        var diagnostics = compilation.GetDiagnostics();

        await Assert.That(diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences(diagnostics)).IsFalse();
    }

    [Test]
    public async Task SyntaxWarningDoesNotSuppressSemanticFallback()
    {
        var compilation = Compile("#warning test\npublic class C { public int M() => Missing; }");
        var diagnostics = compilation.GetDiagnostics();

        await Assert.That(diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Warning)).IsTrue();
        await Assert.That(diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences(diagnostics)).IsTrue();
    }

    [Test]
    public async Task CrossAssemblyPrivateMemberRetainsFallback()
    {
        using var image = new MemoryStream();
        var emitted = Compile("public static class Library { private static int Hidden() => 1; }").Emit(image);
        if (!emitted.Success)
            throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
        var library = MetadataReference.CreateFromImage(image.ToArray());
        var consumer = Compile("public static class C { public static int M() => Library.Hidden(); }", library);
        var diagnostics = consumer.GetDiagnostics();

        await Assert.That(diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences(diagnostics)).IsTrue();
    }

    [Test]
    public async Task UnknownSemanticErrorsRetainCompatibilityFallback()
    {
        var diagnostics = Compile("public class C { public int M() => DoesNotExist(); }").GetDiagnostics();

        await Assert.That(diagnostics.Any(static value => value.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences(diagnostics)).IsTrue();
    }

    [Test]
    public async Task SuccessAndWarningsAloneNeverRequestRetry()
    {
        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences(Compile("public class C {}").GetDiagnostics())).IsFalse();
        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences(Compile("#warning test\npublic class C {}").GetDiagnostics())).IsFalse();
    }

    [Test]
    public async Task DiagnosticWithoutSourceRetainsCompatibilityFallback()
    {
        var descriptor = new DiagnosticDescriptor("CDT0001", "Test", "Test", "Test", DiagnosticSeverity.Error, true);
        var diagnostic = Diagnostic.Create(descriptor, Location.None);

        await Assert.That(DetourCompiler.ShouldRetryWithPublicizedReferences([diagnostic])).IsTrue();
    }

    static CSharpCompilation Compile(string source, params MetadataReference[] additionalReferences)
        => CSharpCompilation.Create(
            "RetryTest_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, path: "test.cs")],
            new[] { core }.Concat(additionalReferences),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
}
