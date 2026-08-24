#nullable enable

namespace Conduit
{
    readonly struct ServerDownloadTarget
    {
        internal ServerDownloadTarget(string url, string destinationPath, bool needsExecutableBit)
        {
            Url = url;
            DestinationPath = destinationPath;
            NeedsExecutableBit = needsExecutableBit;
        }

        internal string Url { get; }
        internal string DestinationPath { get; }
        internal bool NeedsExecutableBit { get; }
    }

}
