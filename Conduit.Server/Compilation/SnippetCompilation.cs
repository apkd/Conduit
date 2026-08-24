namespace Conduit;

readonly record struct SnippetCompilation(
    CompiledSnippet? Compilation,
    ToolExecutionResult? Failure)
{
    internal static SnippetCompilation Success(CompiledSnippet compilation) =>
        new(compilation, null);

    internal static SnippetCompilation FromFailure(ToolExecutionResult failure) =>
        new(null, failure);
}
