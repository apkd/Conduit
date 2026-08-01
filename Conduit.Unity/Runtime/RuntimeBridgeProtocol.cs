#nullable enable

using System.IO;
using Conduit;

namespace Conduit.Runtime
{
    static class RuntimeBridgeArtifactExtensions
    {
        public static byte[] Decode(this BridgeArtifact artifact)
        {
            if (string.IsNullOrWhiteSpace(artifact.relative_path))
                return artifact.DecodeChunks();

            var bytes = File.ReadAllBytes(RuntimeIpcPaths.ResolveRelativePath(artifact.relative_path));
            artifact.Verify(bytes);
            return bytes;
        }
    }
}
