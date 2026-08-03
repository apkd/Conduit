namespace Conduit;

/// <summary>Stages compiled snippet artifacts in the shared player endpoint directory.</summary>
static class PlayerArtifactStore
{
    internal static BridgeArtifact[] Materialize(
        BridgeEndpointDescriptor endpoint,
        IReadOnlyCollection<BridgeArtifact> artifacts)
    {
        var materialized = new BridgeArtifact[artifacts.Count];
        var index = 0;
        foreach (var artifact in artifacts)
        {
            if (artifact.Content != null)
                artifact.MaterializeInEndpoint(endpoint.EndpointDirectoryPath);
            else
                artifact.ResolveInEndpoint(endpoint.EndpointDirectoryPath);

            materialized[index++] = artifact;
        }

        return materialized;
    }
}
