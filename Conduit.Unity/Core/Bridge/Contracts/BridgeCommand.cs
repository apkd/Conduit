#nullable enable

using System;

namespace Conduit
{
    [Serializable]
    sealed class BridgeCommand
    {
        public string command_type = string.Empty;
        public string? target;
        public string? snippet;
        public string? display_name;
        public string? test_filter;
        public bool @async;
        public bool rebuild_cache;
        public bool track_usage;
        public bool include_background_logs;
        public string[] args = Array.Empty<string>();
        public BridgeArtifact[] artifacts = Array.Empty<BridgeArtifact>();

        public string CommandType { get => command_type; set => command_type = value; }
        public string? Target { get => target; set => target = value; }
        public string? Snippet { get => snippet; set => snippet = value; }
        public string? DisplayName { get => display_name; set => display_name = value; }
        public string? TestFilter { get => test_filter; set => test_filter = value; }
        public bool? Async { get => @async ? true : null; set => @async = value == true; }
        public bool? RebuildCache { get => rebuild_cache ? true : null; set => rebuild_cache = value == true; }
        public bool? TrackUsage { get => track_usage ? true : null; set => track_usage = value == true; }
        public bool? IncludeBackgroundLogs { get => include_background_logs ? true : null; set => include_background_logs = value == true; }
        public string[] Args { get => args; set => args = value; }
        public BridgeArtifact[] Artifacts { get => artifacts; set => artifacts = value; }
    }
}
