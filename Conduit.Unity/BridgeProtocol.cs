#nullable enable

using System;

namespace Conduit
{
    static class BridgeArtifactExtensions
    {
        public static byte[] Decode(this BridgeArtifact artifact)
        {
            if (artifact.Content == null && artifact.ResolvedPath == null)
                artifact.ResolveInProject(ConduitAssetPathUtility.GetProjectRootPath());

            return artifact.ReadVerified();
        }
    }

    [Serializable]
    sealed class PendingOperationState
    {
        public string request_id = string.Empty;
        public string command_type = string.Empty;
        public BridgeCommandKind kind;
        public string? target;
        public string? snippet;
        public string? display_name;
        public string? test_filter;
        public bool @async;
        public bool rebuild_cache;
        public bool is_restored;
        public int client_id;
        public bool is_acknowledged;
        // build-target and define changes reload the domain; retain the original value for the final confirmation.
        public string? project_setting_previous;
        // session state preserves this timestamp across play-mode and compilation domain reloads.
        public long tool_usage_started_utc_ticks;
        public string[] args = Array.Empty<string>();
        public BridgeArtifact[] artifacts = Array.Empty<BridgeArtifact>();
        public string[] reimport_asset_paths = Array.Empty<string>();
    }

    [Serializable]
    sealed class ReferenceCacheDocument
    {
        public string cached_at_utc = string.Empty;
        public SerializableLookupEntry[] entries = Array.Empty<SerializableLookupEntry>();
    }

    [Serializable]
    sealed class SerializableLookupEntry
    {
        public string guid = string.Empty;
        public string[] referencer_guids = Array.Empty<string>();
    }
}
