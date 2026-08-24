namespace Conduit;

sealed record CompiledSnippet(
    string SourceFileName,
    string FullTypeName,
    BridgeArtifact Assembly,
    BridgeArtifact Pdb,
    string? Warning)
{
    internal BridgeCommand ToCommand() =>
        new()
        {
            CommandType = BridgeCommandTypes.ExecuteCode,
            Target = FullTypeName,
            DisplayName = SourceFileName,
            Artifacts = [Assembly, Pdb],
        };
}
