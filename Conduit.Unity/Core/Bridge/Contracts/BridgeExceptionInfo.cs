#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class BridgeExceptionInfo
    {
        public string type = string.Empty;
        public string message = string.Empty;
        public string? stack_trace;

        public string Type { get => type; set => type = value; }
        public string Message { get => message; set => message = value; }
        public string? StackTrace { get => stack_trace; set => stack_trace = value; }
    }
}
