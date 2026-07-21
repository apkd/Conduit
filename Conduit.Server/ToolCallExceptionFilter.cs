using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Conduit;

static class ToolCallExceptionFilter
{
    const string MissingArgumentPrefix = "The arguments dictionary is missing a value for the required parameter ";

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Apply(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next
    )
        => async (request, ct) =>
        {
            try
            {
                return await next(request, ct);
            }
            catch (ArgumentException exception) when (
                exception.ParamName == "arguments"
                && exception.Message.StartsWith(MissingArgumentPrefix, StringComparison.Ordinal)
            )
            {
                var argument = exception.Message[MissingArgumentPrefix.Length..].Split('.', 2)[0];
                throw new McpException(
                    $"Invalid arguments for '{request.Params?.Name}': missing required argument {argument}.",
                    exception
                );
            }
        };
}
