using System.Text.Json.Serialization;

namespace Conduit;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower
)]
[JsonSerializable(typeof(BridgeMessage))]
[JsonSerializable(typeof(BridgeProjectHandshake))]
[JsonSerializable(typeof(BridgeCommand))]
[JsonSerializable(typeof(BridgeCommandResult))]
[JsonSerializable(typeof(BridgeExceptionInfo))]
[JsonSerializable(typeof(BridgeArtifact))]
[JsonSerializable(typeof(BridgeEndpointDescriptor))]
[JsonSerializable(typeof(BridgeAssemblyReferenceManifest))]
[JsonSerializable(typeof(BridgeAssemblyReference))]
[JsonSerializable(typeof(UnityPingSnapshot))]
[JsonSerializable(typeof(RecentProjectDocument))]
[JsonSerializable(typeof(RecentProjectRecord))]
[JsonSerializable(typeof(ToolExecutionResult))]
[JsonSerializable(typeof(ToolExceptionInfo))]
[JsonSerializable(typeof(ProjectListItem))]
[JsonSerializable(typeof(ProfilerRecordAction))]
[JsonSerializable(typeof(ProfilerOverviewMode))]
[JsonSerializable(typeof(ProfilerBrowseSort))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(float))]
partial class ConduitJsonContext : JsonSerializerContext { }
