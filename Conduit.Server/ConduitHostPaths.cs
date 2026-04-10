namespace Conduit;

static class ConduitHostPaths
{
    public static string GetServerLogPath()
        => Path.Combine(Path.GetTempPath(), "Conduit", "conduit-mcp-server.log");
}
