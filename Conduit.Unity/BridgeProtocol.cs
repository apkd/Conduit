#nullable enable

using System;
using System.IO;

namespace Conduit
{
    static class BridgeArtifactExtensions
    {
        public static byte[] Decode(this BridgeArtifact artifact)
        {
            if (string.IsNullOrWhiteSpace(artifact.relative_path))
                return artifact.DecodeChunks();

            var bytes = ReadProjectFile(artifact.relative_path!);
            artifact.Verify(bytes);
            return bytes;
        }

        static byte[] ReadProjectFile(string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
                throw new InvalidOperationException($"Artifact '{relativePath}' must use a project-relative path.");

            var projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
            var path = Path.GetFullPath(Path.Combine(projectRoot, relativePath));
            var normalized = Path.GetRelativePath(projectRoot, path);
            // the server may only reference artifacts it wrote inside this Unity project.
            if (Path.IsPathRooted(normalized)
                || normalized == ".."
                || normalized.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException($"Artifact '{relativePath}' resolves outside the Unity project.");

            return File.ReadAllBytes(path);
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
