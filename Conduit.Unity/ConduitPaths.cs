#nullable enable

using System.IO;

namespace Conduit
{
    static class ConduitPaths
    {
        public static string GetDiagnosticsLogPath()
            => Path.Combine(Path.GetTempPath(), "Conduit", ConduitProjectIdentity.GetPipeName(), "conduit-client.log");

        public static string GetReferenceCachePath()
            => Path.Combine(ConduitAssetPathUtility.GetProjectRootPath(), "Library", "Conduit.ReferenceCache.json");
    }
}
