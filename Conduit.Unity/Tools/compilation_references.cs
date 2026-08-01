#nullable enable

namespace Conduit
{
    static class compilation_references
    {
        internal static BridgeCommandResult GetManifest() => AssemblyReferences.GetManifest();

        internal static BridgeCommandResult GetAssemblyBlob(string? referenceId)
            => AssemblyReferences.GetAssemblyBlob(referenceId);
    }
}
