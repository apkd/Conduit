#nullable enable

using System.IO;

namespace Conduit
{
    static class BridgeFifoStreams
    {
        // let StreamReader buffer input: Mono refills its own buffer before returning a short read
        internal static FileStream OpenRead(string path, bool asynchronous = true)
            => new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1,
                asynchronous ? FileOptions.Asynchronous : FileOptions.None
            );
    }
}
