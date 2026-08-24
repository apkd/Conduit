#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class PersistedPendingResultState
    {
        public string RequestID = string.Empty;
        public string CommandType = string.Empty;
        public BridgeCommandResult Result = new();
    }
}
