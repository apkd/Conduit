#nullable enable

#if UNITY_EDITOR
namespace Conduit
{
    readonly struct McpToolCallResult
    {
        public McpToolCallResult(bool isError, string text)
        {
            IsError = isError;
            Text = text;
        }

        public bool IsError { get; }

        public string Text { get; }
    }
}
#endif
