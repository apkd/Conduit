#nullable enable

using System;

namespace Conduit
{
    sealed class PendingOperationState
    {
        internal string RequestID = string.Empty;
        internal string CommandType = string.Empty;
        internal BridgeCommandKind Kind;
        internal string? Target;
        internal string? Snippet;
        internal string? DisplayName;
        internal string? TestFilter;
        internal bool IsAsync;
        internal bool RebuildCache;
        internal bool IsRestored;
        internal int ClientID;
        internal bool IsAcknowledged;
        // build-target and define changes reload the domain; retain the original value for the final confirmation.
        internal string? ProjectSettingPrevious;
        // session state preserves this timestamp across play-mode and compilation domain reloads.
        internal long ToolUsageStartedUtcTicks;
        internal string[] Args = Array.Empty<string>();
        internal BridgeArtifact[] Artifacts = Array.Empty<BridgeArtifact>();
        internal string[] ReimportAssetPaths = Array.Empty<string>();
    }
}
