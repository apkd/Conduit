using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Conduit;

static class ToolCallArgumentValidationFilter
{
    static readonly Dictionary<string, Dictionary<string, Type>> ToolParameterTypes = typeof(UnityTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
        .Where(item => item.Attribute?.Name is not null)
        .ToDictionary(
            item => item.Attribute!.Name!,
            item => item.Method
                .GetParameters()
                .Where(parameter => parameter.Name is not null)
                .ToDictionary(parameter => parameter.Name!, parameter => parameter.ParameterType, StringComparer.Ordinal),
            StringComparer.Ordinal
        );

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Apply(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next
    )
        => async (request, ct) =>
        {
            ValidateArguments(request);
            return await next(request, ct);
        };

    static void ValidateArguments(RequestContext<CallToolRequestParams> request)
    {
        if (
            request.Params is not { Name: { Length: > 0 } toolName } parameters
            || request.MatchedPrimitive is not McpServerTool tool
            || tool.ProtocolTool.InputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out var properties)
        )
            return;

        var arguments = parameters.Arguments;
        var errors = new List<string>();

        if (schema.TryGetProperty("required", out var required))
            foreach (var requiredProperty in required.EnumerateArray())
            {
                var name = requiredProperty.GetString()!;
                if (arguments is null || !arguments.ContainsKey(name))
                    errors.Add($"missing required argument '{name}'");
            }

        if (arguments is not null)
            foreach (var (name, value) in arguments)
            {
                if (!properties.TryGetProperty(name, out var propertySchema))
                {
                    var expected = string.Join(
                        ", ",
                        properties.EnumerateObject().Select(property => $"'{property.Name}'")
                    );
                    errors.Add(
                        expected.Length == 0
                            ? $"unknown argument '{name}' (this tool accepts no arguments)"
                            : $"unknown argument '{name}' (expected arguments: {expected})"
                    );
                    continue;
                }

                ValidateArgument(toolName, name, value, propertySchema, errors);
            }

        if (errors.Count > 0)
            throw new McpException($"Invalid arguments for '{toolName}': {string.Join("; ", errors)}.");
    }

    static void ValidateArgument(
        string toolName,
        string name,
        JsonElement value,
        JsonElement schema,
        List<string> errors
    )
    {
        if (!AcceptsValueKind(schema, value.ValueKind))
        {
            var expected = DescribeExpectedValue(schema, toolName, name);
            errors.Add(
                value.ValueKind == JsonValueKind.Null
                    ? $"argument '{name}' cannot be null; expected {expected}"
                    : $"argument '{name}' must be {expected}, but received {DescribeValueKind(value.ValueKind)}"
            );
            return;
        }

        if (
            schema.TryGetProperty("enum", out var allowedValues)
            && !allowedValues.EnumerateArray().Any(allowed => allowed.GetRawText() == value.GetRawText())
        )
        {
            errors.Add($"argument '{name}' must be {DescribeExpectedValue(schema, toolName, name)}");
            return;
        }

        if (
            !ToolParameterTypes.TryGetValue(toolName, out var parameterTypes)
            || !parameterTypes.TryGetValue(name, out var parameterType)
        )
            return;

        if (!IsValidClrValue(value, parameterType))
            errors.Add($"argument '{name}' must be {DescribeExpectedValue(schema, toolName, name)}");
    }

    static bool IsValidClrValue(JsonElement value, Type parameterType)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return true;

        parameterType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        if (parameterType == typeof(int))
            return value.TryGetInt32(out _);
        if (parameterType == typeof(double))
            return value.TryGetDouble(out var number) && double.IsFinite(number);
        return true;
    }

    static bool AcceptsValueKind(JsonElement schema, JsonValueKind valueKind)
    {
        if (!schema.TryGetProperty("type", out var type))
            return true;

        return type.ValueKind switch
        {
            JsonValueKind.String => AcceptsType(type.GetString()!, valueKind),
            JsonValueKind.Array => type.EnumerateArray().Any(item => AcceptsType(item.GetString()!, valueKind)),
            _ => true,
        };
    }

    static bool AcceptsType(string type, JsonValueKind valueKind)
        => type switch
        {
            "array" => valueKind == JsonValueKind.Array,
            "boolean" => valueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" or "number" => valueKind == JsonValueKind.Number,
            "null" => valueKind == JsonValueKind.Null,
            "object" => valueKind == JsonValueKind.Object,
            "string" => valueKind == JsonValueKind.String,
            _ => true,
        };

    static string DescribeExpectedValue(JsonElement schema, string toolName, string argumentName)
    {
        if (schema.TryGetProperty("enum", out var allowedValues))
            return $"one of {string.Join(", ", allowedValues.EnumerateArray().Select(value => $"'{value.GetString()}'"))}";

        Type? parameterType = null;
        if (
            ToolParameterTypes.TryGetValue(toolName, out var parameterTypes)
            && parameterTypes.TryGetValue(argumentName, out var declaredType)
        )
            parameterType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        if (parameterType == typeof(int))
            return "a 32-bit integer";
        if (parameterType == typeof(double))
            return "a finite number";

        if (!schema.TryGetProperty("type", out var type))
            return "a valid value";

        return type.ValueKind switch
        {
            JsonValueKind.String => DescribeSchemaType(type.GetString()!),
            JsonValueKind.Array => string.Join(
                " or ",
                type.EnumerateArray().Select(item => DescribeSchemaType(item.GetString()!))
            ),
            _ => "a valid value",
        };
    }

    static string DescribeSchemaType(string type)
        => type switch
        {
            "array" => "an array",
            "boolean" => "a boolean",
            "integer" => "an integer",
            "null" => "null",
            "number" => "a number",
            "object" => "an object",
            "string" => "a string",
            _ => "a valid value",
        };

    static string DescribeValueKind(JsonValueKind valueKind)
        => valueKind switch
        {
            JsonValueKind.Array => "an array",
            JsonValueKind.False or JsonValueKind.True => "a boolean",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => "a number",
            JsonValueKind.Object => "an object",
            JsonValueKind.String => "a string",
            _ => "an invalid value",
        };
}
