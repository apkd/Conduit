#nullable enable

using System.Collections.Generic;

namespace Conduit
{
    sealed class ClientWorkSnapshot
    {
        internal static readonly ClientWorkSnapshot Empty = new(null, -1, -1, null, false);
        readonly int activeClientId;
        readonly int firstQueuedClientId;
        readonly int[]? additionalQueuedClientIds;
        readonly bool hasReconnectableWork;

        ClientWorkSnapshot(
            string? activeCommandType,
            int activeClientId,
            int firstQueuedClientId,
            int[]? additionalQueuedClientIds,
            bool hasReconnectableWork)
        {
            ActiveCommandType = activeCommandType;
            this.activeClientId = activeClientId;
            this.firstQueuedClientId = firstQueuedClientId;
            this.additionalQueuedClientIds = additionalQueuedClientIds;
            this.hasReconnectableWork = hasReconnectableWork;
        }

        internal string? ActiveCommandType { get; }

        internal static ClientWorkSnapshot Create(
            PendingOperationState? activeOperation,
            List<PendingOperationState> queuedOperations,
            bool hasPendingResult)
        {
            var firstQueuedClientId = queuedOperations.Count == 0
                ? -1
                : queuedOperations[0].ClientID;
            var additionalQueuedClientIds = queuedOperations.Count <= 1
                ? null
                : new int[queuedOperations.Count - 1];
            var hasReconnectableWork = hasPendingResult
                                       || activeOperation?.ClientID == 0
                                       || firstQueuedClientId == 0;
            for (var index = 1; index < queuedOperations.Count; index++)
            {
                var clientId = queuedOperations[index].ClientID;
                additionalQueuedClientIds![index - 1] = clientId;
                hasReconnectableWork |= clientId == 0;
            }

            return new(
                activeOperation?.CommandType,
                activeOperation?.ClientID ?? -1,
                firstQueuedClientId,
                additionalQueuedClientIds,
                hasReconnectableWork
            );
        }

        internal bool HasOutstandingClientWork(int clientId)
        {
            if (clientId <= 0)
                return false;

            if (activeClientId == clientId)
                return true;

            if (firstQueuedClientId == clientId)
                return true;

            if (additionalQueuedClientIds == null)
                return false;

            foreach (var queuedClientId in additionalQueuedClientIds)
                if (queuedClientId == clientId)
                    return true;

            return false;
        }

        internal bool HasReconnectableWorkForAnyClient() => hasReconnectableWork;
    }
}
