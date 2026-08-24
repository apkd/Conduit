#nullable enable

using System;
using System.Globalization;

namespace Conduit
{
    /// <summary>Identifies a connected Unity Editor or player process.</summary>
    [Serializable]
    public sealed class BridgeProjectHandshake
    {
        public string project_path = string.Empty;
        public string display_name = string.Empty;
        public string unity_version = string.Empty;
        public int editor_process_id;
        public int process_id;
        public string endpoint_kind = BridgeEndpointKinds.Editor;
        public string platform = string.Empty;
        public string build_guid = string.Empty;
        public string cloud_project_id = string.Empty;
        public string company_name = string.Empty;
        public string product_name = string.Empty;
        public bool can_monitor_process = true;
        public bool preserve_snippets;
        public string editor_log_path = string.Empty;
        public string session_instance_id = string.Empty;
        public string handoff_token = string.Empty;
        public string last_seen_utc = string.Empty;

        public string ProjectPath { get => project_path; set => project_path = value; }
        public string DisplayName { get => display_name; set => display_name = value; }
        public string UnityVersion { get => unity_version; set => unity_version = value; }
        public int EditorProcessId { get => editor_process_id; set => editor_process_id = value; }
        public int ProcessId { get => process_id; set => process_id = value; }
        public string EndpointKind { get => endpoint_kind; set => endpoint_kind = value; }
        public string Platform { get => platform; set => platform = value; }
        public string BuildGuid { get => build_guid; set => build_guid = value; }
        public string CloudProjectId { get => cloud_project_id; set => cloud_project_id = value; }
        public string CompanyName { get => company_name; set => company_name = value; }
        public string ProductName { get => product_name; set => product_name = value; }
        public bool CanMonitorProcess { get => can_monitor_process; set => can_monitor_process = value; }
        public bool PreserveSnippets { get => preserve_snippets; set => preserve_snippets = value; }
        public string EditorLogPath { get => editor_log_path; set => editor_log_path = value; }
        public string SessionInstanceId { get => session_instance_id; set => session_instance_id = value; }
        public string HandoffToken { get => handoff_token; set => handoff_token = value; }
        public DateTimeOffset LastSeenUtc
        {
            get => DateTimeOffset.TryParse(last_seen_utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : default;
            set => last_seen_utc = value == default ? string.Empty : value.ToString("O", CultureInfo.InvariantCulture);
        }

        internal int EffectiveProcessId => process_id > 0 ? process_id : editor_process_id;
        internal bool IsPlayer => endpoint_kind == BridgeEndpointKinds.Player;
    }
}
