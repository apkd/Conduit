using System.Text.Json.Serialization;

namespace Conduit;

[JsonConverter(typeof(JsonStringEnumConverter<ProfilerBrowseSort>))]
public enum ProfilerBrowseSort
{
    [JsonStringEnumMemberName("total_ms")]
    TotalMs,

    [JsonStringEnumMemberName("self_ms")]
    SelfMs,

    [JsonStringEnumMemberName("gc_bytes")]
    GcBytes,

    [JsonStringEnumMemberName("calls")]
    Calls,
}
