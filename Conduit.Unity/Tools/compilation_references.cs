#nullable enable

using System.IO;

namespace Conduit
{
    static class compilation_references
    {
        internal static BridgeCommandResult GetManifest() => AssemblyReferences.GetManifest();

        internal static BridgeCommandResult GetAssemblyBlobs(string[] referenceIds)
        {
            var result = AssemblyReferences.GetAssemblyBlobs(referenceIds);
            if (result.outcome != ToolOutcome.Success)
                return result;

            var relativeDirectory = Path.Combine("Temp", "Conduit", "references");
            var projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
            var directory = Path.Combine(projectRoot, relativeDirectory);
            Directory.CreateDirectory(directory);
            for (var index = 0; index < result.artifacts.Length; index++)
            {
                var artifact = result.artifacts[index];
                var bytes = artifact.ReadVerified();
                var relativePath = Path.Combine(
                    relativeDirectory,
                    artifact.sha256 + ".dll"
                );
                File.WriteAllBytes(Path.Combine(projectRoot, relativePath), bytes);
                result.artifacts[index] = BridgeArtifact.FromProjectFile(
                    artifact.name,
                    artifact.media_type,
                    relativePath,
                    bytes
                );
            }

            return result;
        }
    }
}
