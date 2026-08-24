namespace Conduit;

readonly record struct SnippetParseResult(
    IReadOnlyList<SnippetChunk> Usings,
    IReadOnlyList<SnippetChunk> TypeDeclarations,
    IReadOnlyList<SnippetChunk> StaticFields,
    SnippetChunk Body
);
