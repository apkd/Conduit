using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Conduit;

static class CompilationMetadata
{
    internal static PortableExecutableReference CreateReference(string path)
    {
        using var stream = File.OpenRead(path);
        return CreateReference(stream, path);
    }

    // compilation needs a stable metadata snapshot, without retaining method bodies, resources, or file locks.
    internal static PortableExecutableReference CreateReference(Stream stream, string path)
        => AssemblyMetadata.CreateFromStream(stream, PEStreamOptions.PrefetchMetadata)
            .GetReference(filePath: path);
}
