using System.Text.Json.Serialization;

namespace Conduit;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
)]
[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(BridgeProjectHandshake))]
[JsonSerializable(typeof(BridgeCommand))]
[JsonSerializable(typeof(BridgeCommandResult))]
[JsonSerializable(typeof(BridgeExceptionInfo))]
[JsonSerializable(typeof(UnityPingSnapshot))]
[JsonSerializable(typeof(RecentProjectDocument))]
[JsonSerializable(typeof(RecentProjectRecord))]
[JsonSerializable(typeof(ToolExecutionResult))]
[JsonSerializable(typeof(ToolExceptionInfo))]
[JsonSerializable(typeof(ProjectListItem))]
[JsonSerializable(typeof(ProfilerRecordAction))]
[JsonSerializable(typeof(ProfilerOverviewMode))]
[JsonSerializable(typeof(ProfilerBrowseSort))]
partial class ConduitJsonContext : JsonSerializerContext { }
