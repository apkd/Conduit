#nullable enable

using Conduit;

namespace Conduit.Runtime
{
    static class RuntimeBridgeArtifactExtensions
    {
        public static byte[] Decode(this BridgeArtifact artifact)
            => artifact.ReadVerified();
    }
}
