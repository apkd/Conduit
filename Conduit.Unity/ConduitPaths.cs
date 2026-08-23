#nullable enable

using System.IO;

namespace Conduit
{
    static class ConduitPaths
    {
        static readonly string diagnosticsLogPath = Path.Combine(
            Path.GetTempPath(),
            "Conduit",
            ConduitProjectIdentity.GetPipeName(),
            "conduit-client.log"
        );
        static readonly string referenceCachePath = Path.Combine(
            ConduitAssetPathUtility.GetProjectRootPath(),
            "Library",
            "Conduit.ReferenceCache.json"
        );

        public static string GetDiagnosticsLogPath()
            => diagnosticsLogPath;

        public static string GetReferenceCachePath()
            => referenceCachePath;
    }
}
