namespace Conduit;

readonly record struct PreparedDetour(
    BridgeCommand? Command,
    ToolExecutionResult? Failure,
    string? Warning
)
{
    internal static PreparedDetour Succeeded(BridgeCommand command, string? warning = null) =>
        new(command, null, warning);

    internal static PreparedDetour Failed(ToolExecutionResult failure) => new(null, failure, null);
}
