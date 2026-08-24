#nullable enable

using System;
using System.Threading.Tasks;

namespace Conduit
{
    static class ExecuteCodeTool
    {
        internal static Task<BridgeCommandResult> ExecuteAsync(PendingOperationState operation)
            => CompiledSnippetRunner.ExecuteAsync(
                operation.Artifacts,
                operation.Target,
                operation.DisplayName,
                static artifact => artifact.Decode()
            );

        internal static bool ShouldSuppressCompilerWarning(string message)
            => message.Contains(" warning MED011:", StringComparison.Ordinal);
    }
}
