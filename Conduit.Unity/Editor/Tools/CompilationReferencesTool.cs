#nullable enable

using System.IO;

namespace Conduit
{
    static class CompilationReferencesTool
    {
        internal static BridgeCommandResult GetManifest() =>
            AssemblyReferences.GetManifest(ConduitSnippetStorage.PreserveSnippets);

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
                var bytes = artifact.Content
                            ?? throw new InvalidDataException(
                                $"Assembly reference '{artifact.name}' had no in-memory content."
                            );
                var relativePath = Path.Combine(
                    relativeDirectory,
                    artifact.sha256 + ".dll"
                );
                File.WriteAllBytes(Path.Combine(projectRoot, relativePath), bytes);
                // reuse the hash computed while reading; the receiving server verifies the file.
                result.artifacts[index] = artifact.AsProjectFile(relativePath);
            }

            return result;
        }
    }
}
