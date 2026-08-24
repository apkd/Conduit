using System.Text.Json.Serialization;

namespace Conduit;

[JsonConverter(typeof(JsonStringEnumConverter<ProfilerOverviewMode>))]
public enum ProfilerOverviewMode
{
    [JsonStringEnumMemberName("cpu_ms")]
    CpuMs,

    [JsonStringEnumMemberName("gc_kb")]
    GcKb,
}
