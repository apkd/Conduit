using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class PlayerSnippetCompilerTests
{
    [Test]
    public async Task AccessiblePlayerReferenceUsesItsExactPath()
    {
        var path = typeof(PlayerSnippetCompilerTests).Assembly.Location;
        var reference = new RuntimeAssemblyReference
        {
            Id = "the runtime identity is only needed when transferring the file",
            Path = path,
            Length = new FileInfo(path).Length,
        };

        await Assert.That(PlayerSnippetCompiler.TryResolveAccessiblePath(reference))
            .IsEqualTo(path);
    }

    [Test]
    public async Task AccessiblePlayerReferenceRejectsDifferentFileLength()
    {
        var path = typeof(PlayerSnippetCompilerTests).Assembly.Location;
        var reference = new RuntimeAssemblyReference
        {
            Path = path,
            Length = new FileInfo(path).Length + 1,
        };

        await Assert.That(PlayerSnippetCompiler.TryResolveAccessiblePath(reference)).IsNull();
    }

    [Test]
    public async Task ProtonZDriveReferenceMapsToItsLinuxHostPath()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = typeof(PlayerSnippetCompilerTests).Assembly.Location;
        var reference = new RuntimeAssemblyReference
        {
            Path = "Z:" + path.Replace('/', '\\'),
            Length = new FileInfo(path).Length,
        };

        await Assert.That(PlayerSnippetCompiler.TryResolveAccessiblePath(reference))
            .IsEqualTo(path);
    }
}
