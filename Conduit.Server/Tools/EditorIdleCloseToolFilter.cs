using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Conduit;

static class EditorIdleCloseToolFilter
{
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Apply(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next
    ) => (request, ct) =>
    {
        // compilation and server-only tools can fail before normal bridge preflight runs
        if (request.Params is { Name: not BridgeCommandTypes.Restart, Arguments: { } arguments }
            && arguments.TryGetValue("projectPath", out var target)
            && target.ValueKind == JsonValueKind.String
            && request.Services!.GetRequiredService<UnityProjectOperations>()
                .GetIdleCloseDiagnostic(target.GetString()!) is { } diagnostic)
        {
            return ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = diagnostic }],
            });
        }

        return next(request, ct);
    };
}
