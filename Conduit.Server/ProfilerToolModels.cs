using System.Text.Json.Serialization;

namespace Conduit;

[JsonConverter(typeof(JsonStringEnumConverter<ProfilerRecordAction>))]
public enum ProfilerRecordAction
{
    [JsonStringEnumMemberName("capture")]
    Capture,

    [JsonStringEnumMemberName("save")]
    Save,

    [JsonStringEnumMemberName("load")]
    Load,

    [JsonStringEnumMemberName("list")]
    List,
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfilerOverviewMode>))]
public enum ProfilerOverviewMode
{
    [JsonStringEnumMemberName("cpu_ms")]
    CpuMs,

    [JsonStringEnumMemberName("gc_kb")]
    GcKb,
}

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

static class ProfilerToolModelExtensions
{
    public static string ToWireName(this ProfilerRecordAction action) =>
        action switch
        {
            ProfilerRecordAction.Capture => "capture",
            ProfilerRecordAction.Save    => "save",
            ProfilerRecordAction.Load    => "load",
            ProfilerRecordAction.List    => "list",
            _                            => action.ToString(),
        };

    public static string ToWireName(this ProfilerOverviewMode mode) =>
        mode switch
        {
            ProfilerOverviewMode.CpuMs => "cpu_ms",
            ProfilerOverviewMode.GcKb  => "gc_kb",
            _                          => mode.ToString(),
        };

    public static string ToWireName(this ProfilerBrowseSort sort) =>
        sort switch
        {
            ProfilerBrowseSort.TotalMs => "total_ms",
            ProfilerBrowseSort.SelfMs  => "self_ms",
            ProfilerBrowseSort.GcBytes => "gc_bytes",
            ProfilerBrowseSort.Calls   => "calls",
            _                          => sort.ToString(),
        };
}
