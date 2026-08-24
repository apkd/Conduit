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
