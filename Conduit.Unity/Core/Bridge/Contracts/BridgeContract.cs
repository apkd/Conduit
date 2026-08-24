#nullable enable

namespace Conduit
{
    /// <summary>Defines the bridge wire version shared by the server, Editor, and player.</summary>
    static class BridgeContract
    {
        public const int Version = 8;

        public static string FormatProtocolMismatch(int serverVersion, int unityVersion)
            => serverVersion < unityVersion
                ? $"Conduit server protocol {serverVersion} is older than Unity Editor bridge protocol {unityVersion}."
                : $"Unity Editor bridge protocol {unityVersion} is older than Conduit server protocol {serverVersion}.";
    }
}
