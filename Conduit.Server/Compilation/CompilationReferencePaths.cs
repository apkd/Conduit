using Microsoft.CodeAnalysis;

namespace Conduit;

readonly record struct CompilationReferencePaths(
    MetadataReference[]? References,
    string[]? Paths,
    bool PreserveSnippets,
    string SessionInstanceId,
    ToolExecutionResult? Failure
)
{
    // captures the owner of this exact snapshot, rather than looking up a possibly newer session.
    internal CompilationReferenceSet? ReferenceSet { get; init; }
}
