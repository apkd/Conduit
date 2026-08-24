namespace Conduit;

static class BridgeIdentifiers
{
    /// <summary>Gets the stable bridge endpoint name for a Unity project.</summary>
    internal static string GetPipeName(string? projectPath) =>
        BridgeEndpointNaming.GetPipeName(ProjectPathNormalizer.Normalize(projectPath));

    /// <summary>Creates a compact bridge-safe request identifier.</summary>
    internal static string CreateRequestId() => Guid.NewGuid().ToString("N");
}
