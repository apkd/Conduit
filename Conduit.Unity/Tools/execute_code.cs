#nullable enable

using System;
using System.Threading.Tasks;

namespace Conduit
{
    static class execute_code
    {
        public static Task<BridgeCommandResult> ExecuteAsync(PendingOperationState operation)
            => CompiledSnippetRunner.ExecuteAsync(
                operation.artifacts,
                operation.target,
                operation.display_name,
                static artifact => artifact.Decode()
            );

        internal static bool ShouldSuppressCompilerWarning(string message)
            => message.Contains(" warning MED011:", StringComparison.Ordinal);
    }
}
