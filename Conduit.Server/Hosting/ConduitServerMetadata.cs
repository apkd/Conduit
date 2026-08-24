using System.Reflection;

namespace Conduit;

static class ConduitServerMetadata
{
    public static string GetPackageVersion()
        => typeof(ConduitServerMetadata).Assembly.GetName().Version?.ToString()
           ?? "0.0.0";
}
