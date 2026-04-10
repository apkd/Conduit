using System.Reflection;

namespace Conduit;

static class ConduitServerMetadata
{
    public static string GetDisplayVersion()
        => typeof(ConduitServerMetadata).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? typeof(ConduitServerMetadata).Assembly.GetName().Version?.ToString()
           ?? "0.0.0";
}
