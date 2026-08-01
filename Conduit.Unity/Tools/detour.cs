#nullable enable

namespace Conduit
{
    static class detour
    {
        internal static BridgeCommandResult Execute(PendingOperationState operation)
            => DetourCommandRunner.Execute(
                operation.args,
                operation.artifacts,
                operation.target,
                operation.display_name,
                static artifact => artifact.Decode()
            );
    }
}
