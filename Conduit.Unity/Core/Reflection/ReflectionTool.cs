#nullable enable

namespace Conduit
{
    static class ReflectionTool
    {
        internal static BridgeCommandResult Reflect(string[] args) =>
            ReflectionQueryEngine.Reflect(args);
    }
}
