namespace Conduit;

/// <summary>Stages compiled snippet artifacts in the shared endpoint directory to keep FIFO commands small.</summary>
static class PlayerArtifactStore
{
    internal static BridgeArtifact[] Materialize(
        BridgeEndpointDescriptor endpoint,
        IReadOnlyCollection<BridgeArtifact> artifacts)
    {
        var directory = Path.Combine(endpoint.EndpointDirectoryPath, "artifacts");
        Directory.CreateDirectory(directory);
        var materialized = new BridgeArtifact[artifacts.Count];
        var index = 0;
        foreach (var artifact in artifacts)
        {
            var fileName = artifact.Sha256 + Path.GetExtension(artifact.Name);
            var path = Path.Combine(directory, fileName);
            WriteAtomically(path, artifact.Decode());

            materialized[index++] = new()
            {
                Name = artifact.Name,
                MediaType = artifact.MediaType,
                Sha256 = artifact.Sha256,
                RelativePath = Path.Combine(
                        "endpoints",
                        Path.GetFileName(endpoint.EndpointDirectoryPath),
                        "artifacts",
                        fileName
                    )
                    .Replace('\\', '/'),
            };
        }

        return materialized;
    }

    static void WriteAtomically(string path, byte[] bytes)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }
}
