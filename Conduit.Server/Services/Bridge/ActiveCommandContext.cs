namespace Conduit;

sealed class ActiveCommandContext(string requestId)
{
    public string RequestId { get; } = requestId;
}
