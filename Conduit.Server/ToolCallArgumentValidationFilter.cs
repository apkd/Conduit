using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Conduit;

static class ToolCallArgumentValidationFilter
{
    static readonly ConcurrentDictionary<string, CachedToolSchema> ToolSchemas = new(
        StringComparer.Ordinal
    );
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
        => (request, ct) =>
        {
            ValidateArguments(request);
            return next(request, ct);
        };

    static void ValidateArguments(RequestContext<CallToolRequestParams> request)
    {
        if (
            request.Params is not { Name: { Length: > 0 } toolName } parameters
            || request.MatchedPrimitive is not McpServerTool tool
            || tool.ProtocolTool.InputSchema is not { ValueKind: JsonValueKind.Object } schema
            || !schema.TryGetProperty("properties", out _)
        )
            return;

        var arguments = parameters.Arguments;
        List<string>? errors = null;
        var cachedSchema = ToolSchemas.GetOrAdd(
            toolName,
            static (name, inputSchema) => new(name, inputSchema),
            schema
        );

        foreach (var name in cachedSchema.RequiredProperties)
            if (arguments is null || !arguments.ContainsKey(name))
                (errors ??= []).Add($"missing required argument '{name}'");

        if (arguments is not null)
            foreach (var (name, value) in arguments)
            {
                if (!cachedSchema.Properties.TryGetValue(name, out var propertySchema))
                {
                    (errors ??= []).Add(
                        cachedSchema.ExpectedArguments.Length == 0
                            ? $"unknown argument '{name}' (this tool accepts no arguments)"
                            : $"unknown argument '{name}' (expected arguments: {cachedSchema.ExpectedArguments})"
                    );
                    continue;
                }

                ValidateArgument(name, value, propertySchema, ref errors);
            }

        if (errors is { Count: > 0 })
            throw new McpException($"Invalid arguments for '{toolName}': {string.Join("; ", errors)}.");
    }

    static void ValidateArgument(
        string name,
        JsonElement value,
        CachedArgumentSchema schema,
        ref List<string>? errors
    )
    {
        if (!schema.Accepts(value.ValueKind))
        {
            (errors ??= []).Add(
                value.ValueKind == JsonValueKind.Null
                    ? $"argument '{name}' cannot be null; expected {schema.ExpectedValue}"
                    : $"argument '{name}' must be {schema.ExpectedValue}, but received {DescribeValueKind(value.ValueKind)}"
            );
            return;
        }

        if (schema.AllowedValues is { } allowedValues
            && !ContainsEquivalentValue(allowedValues, value))
        {
            (errors ??= []).Add($"argument '{name}' must be {schema.ExpectedValue}");
            return;
        }

        if (schema.ParameterType is not { } parameterType)
            return;

        if (!IsValidClrValue(value, parameterType))
            (errors ??= []).Add($"argument '{name}' must be {schema.ExpectedValue}");
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

    static bool ContainsEquivalentValue(JsonElement[] values, JsonElement value)
    {
        foreach (var allowed in values)
            if (JsonElement.DeepEquals(allowed, value))
                return true;

        return false;
    }

    static int GetAcceptedKinds(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type))
            return -1;

        if (type.ValueKind == JsonValueKind.String)
            return GetAcceptedKinds(type.GetString()!);
        if (type.ValueKind != JsonValueKind.Array)
            return -1;

        var kinds = 0;
        foreach (var item in type.EnumerateArray())
        {
            var accepted = GetAcceptedKinds(item.GetString()!);
            if (accepted == -1)
                return -1;
            kinds |= accepted;
        }

        return kinds;
    }

    static int GetAcceptedKinds(string type)
        => type switch
        {
            "array" => Kind(JsonValueKind.Array),
            "boolean" => Kind(JsonValueKind.True) | Kind(JsonValueKind.False),
            "integer" or "number" => Kind(JsonValueKind.Number),
            "null" => Kind(JsonValueKind.Null),
            "object" => Kind(JsonValueKind.Object),
            "string" => Kind(JsonValueKind.String),
            _ => -1,
        };

    static int Kind(JsonValueKind value) => 1 << (int)value;

    static string DescribeExpectedValue(JsonElement schema, Type? parameterType)
    {
        if (schema.TryGetProperty("enum", out var allowedValues))
            return $"one of {string.Join(", ", allowedValues.EnumerateArray().Select(value => $"'{value.GetString()}'"))}";

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

    sealed class CachedToolSchema
    {
        internal CachedToolSchema(string toolName, JsonElement schema)
        {
            var properties = schema.GetProperty("properties");
            Properties = new(StringComparer.Ordinal);
            var names = new List<string>();
            ToolParameterTypes.TryGetValue(toolName, out var parameterTypes);
            foreach (var property in properties.EnumerateObject())
            {
                Type? parameterType = null;
                if (parameterTypes != null)
                    parameterTypes.TryGetValue(property.Name, out parameterType);
                Properties.Add(property.Name, new(property.Value, parameterType));
                names.Add($"'{property.Name}'");
            }

            ExpectedArguments = string.Join(", ", names);
            if (!schema.TryGetProperty("required", out var required))
            {
                RequiredProperties = [];
                return;
            }

            RequiredProperties = new string[required.GetArrayLength()];
            var index = 0;
            foreach (var property in required.EnumerateArray())
                RequiredProperties[index++] = property.GetString()!;
        }

        internal Dictionary<string, CachedArgumentSchema> Properties { get; }
        internal string[] RequiredProperties { get; }
        internal string ExpectedArguments { get; }
    }

    readonly struct CachedArgumentSchema
    {
        internal CachedArgumentSchema(JsonElement schema, Type? parameterType)
        {
            ParameterType = parameterType == null
                ? null
                : Nullable.GetUnderlyingType(parameterType) ?? parameterType;
            AcceptedKinds = GetAcceptedKinds(schema);
            ExpectedValue = DescribeExpectedValue(schema, ParameterType);
            if (!schema.TryGetProperty("enum", out var allowedValues))
            {
                AllowedValues = null;
                return;
            }

            AllowedValues = new JsonElement[allowedValues.GetArrayLength()];
            var index = 0;
            foreach (var allowed in allowedValues.EnumerateArray())
                AllowedValues[index++] = allowed.Clone();
        }

        int AcceptedKinds { get; }
        internal JsonElement[]? AllowedValues { get; }
        internal string ExpectedValue { get; }
        internal Type? ParameterType { get; }

        internal bool Accepts(JsonValueKind kind) => (AcceptedKinds & Kind(kind)) != 0;
    }
}
