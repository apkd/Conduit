namespace Conduit;

readonly record struct SourceArtifactResult(SourceArtifact? Artifact, ToolExecutionResult? Failure)
{
    internal static SourceArtifactResult Succeeded(SourceArtifact artifact) => new(artifact, null);
    internal static SourceArtifactResult Failed(ToolExecutionResult failure) => new(null, failure);
}
