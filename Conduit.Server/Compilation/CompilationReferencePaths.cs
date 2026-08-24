using Microsoft.CodeAnalysis;

namespace Conduit;

readonly record struct CompilationReferencePaths(
    MetadataReference[]? References,
    string[]? Paths,
    bool PreserveSnippets,
    string SessionInstanceId,
    ToolExecutionResult? Failure
);
