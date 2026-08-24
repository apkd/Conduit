namespace Conduit;

sealed class QueuedProjectCommand(
    ProjectSession session,
    BridgeCommand command,
    CancellationToken requestCancellation
)
{
    public ProjectSession Session { get; } = session;
    public BridgeCommand Command { get; } = command;
    public CancellationToken RequestCancellation { get; } = requestCancellation;
    public TaskCompletionSource<ToolExecutionResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TrySetResult(ToolExecutionResult result) => Completion.TrySetResult(result);
}
