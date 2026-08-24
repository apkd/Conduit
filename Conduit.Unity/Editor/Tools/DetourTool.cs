#nullable enable

namespace Conduit
{
    static class DetourTool
    {
        internal static BridgeCommandResult Execute(PendingOperationState operation)
            => DetourCommandRunner.Execute(
                operation.Args,
                operation.Artifacts,
                operation.Target,
                operation.DisplayName,
                static artifact => artifact.Decode()
            );
    }
}
