#nullable enable

namespace Conduit
{
    enum ConduitPackageUpdateState
    {
        NotApplicable,
        Current,
        Outdated,
        Unavailable,
    }

    readonly struct ConduitPackageUpdateStatus
    {
        internal ConduitPackageUpdateStatus(
            ConduitPackageUpdateState state,
            string installedHash = "",
            string latestHash = ""
        )
        {
            State = state;
            InstalledHash = installedHash;
            LatestHash = latestHash;
        }

        internal ConduitPackageUpdateState State { get; }
        internal string InstalledHash { get; }
        internal string LatestHash { get; }
    }
}
