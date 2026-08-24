using System.Text.Json.Serialization;

namespace Conduit;

public sealed class ToolExceptionInfo
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; init; }

    public static ToolExceptionInfo FromException(Exception exception) =>
        ConduitText.ToToolExceptionInfo(exception);
}
