#nullable enable

namespace Conduit
{
    static class BridgeArtifactExtensions
    {
        internal static byte[] Decode(this BridgeArtifact artifact)
        {
            if (artifact.Content == null && artifact.ResolvedPath == null)
                artifact.ResolveInProject(ConduitAssetPathUtility.GetProjectRootPath());

            return artifact.ReadVerified();
        }
    }
}
