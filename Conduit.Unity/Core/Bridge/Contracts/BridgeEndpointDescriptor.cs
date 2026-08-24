#nullable enable

using System;
using System.Globalization;

namespace Conduit
{
    [Serializable]
    sealed class BridgeEndpointDescriptor
    {
        public int protocol_version = BridgeContract.Version;
        public string endpoint_kind = string.Empty;
        public string transport = string.Empty;
        public string endpoint_id = string.Empty;
        public string pipe_name = string.Empty;
        public int process_id;
        public string session_instance_id = string.Empty;
        public string handoff_token = string.Empty;
        public string unity_version = string.Empty;
        public string platform = string.Empty;
        public string build_guid = string.Empty;
        public string cloud_project_id = string.Empty;
        public string company_name = string.Empty;
        public string product_name = string.Empty;
        public string started_utc = string.Empty;
        public string last_seen_utc = string.Empty;
        public bool can_monitor_process;
        public bool is_test_player;

        public int ProtocolVersion { get => protocol_version; set => protocol_version = value; }
        public string EndpointKind { get => endpoint_kind; set => endpoint_kind = value; }
        public string Transport { get => transport; set => transport = value; }
        public string EndpointId { get => endpoint_id; set => endpoint_id = value; }
        public string PipeName { get => pipe_name; set => pipe_name = value; }
        public int ProcessId { get => process_id; set => process_id = value; }
        public string SessionInstanceId { get => session_instance_id; set => session_instance_id = value; }
        public string HandoffToken { get => handoff_token; set => handoff_token = value; }
        public string UnityVersion { get => unity_version; set => unity_version = value; }
        public string Platform { get => platform; set => platform = value; }
        public string BuildGuid { get => build_guid; set => build_guid = value; }
        public string CloudProjectId { get => cloud_project_id; set => cloud_project_id = value; }
        public string CompanyName { get => company_name; set => company_name = value; }
        public string ProductName { get => product_name; set => product_name = value; }
        public string StartedUtc { get => started_utc; set => started_utc = value; }
        public string LastSeenUtc { get => last_seen_utc; set => last_seen_utc = value; }
        public bool CanMonitorProcess { get => can_monitor_process; set => can_monitor_process = value; }
        public bool IsTestPlayer { get => is_test_player; set => is_test_player = value; }

        internal string EndpointDirectoryPath { get; set; } = string.Empty;
        internal string Selector => $"player:{process_id.ToString(CultureInfo.InvariantCulture)}";
    }
}
