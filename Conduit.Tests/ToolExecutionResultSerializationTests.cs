using System.Text.Json;
using System.Reflection;

namespace Conduit;

public sealed class ToolExecutionResultSerializationTests
{
    [Test]
    public async Task NormalizeUserFacingTextCanonicalizesCarriageReturns()
    {
        var normalized = ConduitUtility.NormalizeUserFacingText("a\r\nb\rc");

        await Assert.That(normalized).IsEqualTo("a\nb\nc");
    }

    [Test]
    public async Task NormalizeUserFacingTextPreservesJsonQuotes()
    {
        var normalized = ConduitUtility.NormalizeUserFacingText("\"dependencies\": {\r\n  \"dev.tryfinally.conduit\": \"url\",");

        await Assert.That(normalized).IsEqualTo("\"dependencies\": {\n  \"dev.tryfinally.conduit\": \"url\",");
    }

    [Test]
    public async Task BridgeResultDropsEmptyOptionalFields()
    {
        var result = new BridgeCommandResult
        {
            Outcome = ToolOutcome.Success,
            Logs = string.Empty,
            DisplayName = string.Empty,
            ReturnValue = string.Empty,
            Diagnostic = string.Empty,
            Exception = new()
            {
                Type = string.Empty,
                Message = string.Empty,
            },
        }.ToToolExecutionResult();

        await Assert.That(result.Logs).IsNull();
        await Assert.That(result.DisplayName).IsNull();
        await Assert.That(result.ReturnValue).IsNull();
        await Assert.That(result.Diagnostic).IsNull();
        await Assert.That(result.Exception).IsNull();
    }

    [Test]
    public async Task BridgeResultCanonicalizesLineEndings()
    {
        var result = new BridgeCommandResult
        {
            Outcome = ToolOutcome.Exception,
            Logs = "line 1\r\nline 2\rline 3",
            ReturnValue = "value\r\nnext",
            Exception = new()
            {
                Type = "InvalidOperationException",
                Message = "boom\r\nagain",
                StackTrace = "frame 1\r\nframe 2\rframe 3",
            },
        }.ToToolExecutionResult();

        await Assert.That(result.Logs).IsEqualTo("line 1\nline 2\nline 3");
        await Assert.That(result.ReturnValue).IsEqualTo("value\nnext");
        await Assert.That(result.Exception).IsNotNull();
        await Assert.That(result.Exception.Message).IsEqualTo("boom\nagain");
        await Assert.That(result.Exception.StackTrace).IsEqualTo("frame 1\nframe 2\nframe 3");
    }

    [Test]
    public async Task BridgeResultPreservesDisplayName()
    {
        var result = new BridgeCommandResult
        {
            Outcome = ToolOutcome.Success,
            DisplayName = "17.cs",
        }.ToToolExecutionResult();

        await Assert.That(result.DisplayName).IsEqualTo("17.cs");
    }

    [Test]
    public async Task BridgeResultPreservesJsonQuotesInReturnValue()
    {
        var result = new BridgeCommandResult
        {
            Outcome = ToolOutcome.Success,
            ReturnValue = "{\"unity_version\":\"6000.3.10f1\",\"is_updating\":false}",
        }.ToToolExecutionResult();

        await Assert.That(result.ReturnValue).IsEqualTo("{\"unity_version\":\"6000.3.10f1\",\"is_updating\":false}");
    }

    [Test]
    public async Task NormalizePayloadTextCanonicalizesLineEndingsWithoutReplacingQuotes()
    {
        var normalized = ConduitUtility.NormalizePayloadText("{\"a\":\"b\"}\r\n{\"c\":\"d\"}");

        await Assert.That(normalized).IsEqualTo("{\"a\":\"b\"}\n{\"c\":\"d\"}");
    }

    [Test]
    public async Task SerializedToolResultOmitsEmptyOptionalFields()
    {
        var payload = JsonSerializer.Serialize(
            new()
            {
                Outcome = ToolOutcome.Success,
                Logs = null,
                ReturnValue = null,
                Diagnostic = null,
                Exception = null,
            },
            ConduitJsonContext.Default.ToolExecutionResult
        );

        await Assert.That(payload).IsEqualTo("{\"outcome\":\"success\"}");
    }

    [Test]
    public async Task SerializedToolResultUsesLfOnly()
    {
        var payload = JsonSerializer.Serialize(
            new BridgeCommandResult
            {
                Outcome = ToolOutcome.Exception,
                Logs = "log 1\r\nlog 2",
                Exception = new()
                {
                    Type = "InvalidOperationException",
                    Message = "boom\r\nagain",
                    StackTrace = "frame 1\r\nframe 2",
                },
            }.ToToolExecutionResult(),
            ConduitJsonContext.Default.ToolExecutionResult
        );

        await Assert.That(payload).DoesNotContain("\\r");
        await Assert.That(payload).Contains("\\n");
    }

    [Test]
    public async Task SerializedBridgeCommandIncludesAsyncFlagWhenEnabled()
    {
        var payload = JsonSerializer.Serialize(
            new BridgeCommand
            {
                CommandType = BridgeCommandTypes.RunTestsEditMode,
                Async = true,
            },
            ConduitJsonContext.Default.BridgeCommand
        );

        await Assert.That(payload).Contains("\"async\":true");
    }

    [Test]
    public async Task SerializedProjectSettingsCommandIncludesOperationAndEmptyValue()
    {
        string payload = JsonSerializer.Serialize(
            new BridgeCommand
            {
                CommandType = BridgeCommandTypes.ProjectSettings,
                Snippet = string.Empty,
                Args = ["set"],
            },
            ConduitJsonContext.Default.BridgeCommand
        );

        await Assert.That(payload).Contains("\"snippet\":\"\"");
        await Assert.That(payload).Contains("\"args\":[\"set\"]");
        await Assert.That(payload).DoesNotContain("has_snippet");
    }

    [Test]
    public async Task SerializedToolResultKeepsMeaningfulOptionalFields()
    {
        var payload = JsonSerializer.Serialize(
            new()
            {
                Outcome = ToolOutcome.Exception,
                DisplayName = "17.cs",
                Logs = "captured log",
                ReturnValue = "value",
                Diagnostic = "diagnostic",
                Exception = new()
                {
                    Type = "InvalidOperationException",
                    Message = "boom",
                },
            },
            ConduitJsonContext.Default.ToolExecutionResult
        );

        await Assert.That(payload).Contains("\"display_name\":\"17.cs\"");
        await Assert.That(payload).Contains("\"logs\":\"captured log\"");
        await Assert.That(payload).Contains("\"return_value\":\"value\"");
        await Assert.That(payload).Contains("\"diagnostic\":\"diagnostic\"");
        await Assert.That(payload).Contains("\"exception\":{\"type\":\"InvalidOperationException\",\"message\":\"boom\"}");
    }

    [Test]
    public async Task SerializedToolResultOmitsEmptyExceptionMembers()
    {
        var payload = JsonSerializer.Serialize(
            new()
            {
                Outcome = ToolOutcome.Exception,
                Exception = new()
                {
                    StackTrace = "trace only",
                },
            },
            ConduitJsonContext.Default.ToolExecutionResult
        );

        await Assert.That(payload).Contains("\"exception\":{\"stack_trace\":\"trace only\"}");
        await Assert.That(payload).DoesNotContain("\"type\":\"\"");
        await Assert.That(payload).DoesNotContain("\"message\":\"\"");
    }

    [Test]
    public async Task BridgeCommandSerializesArgs()
    {
        var payload = JsonSerializer.Serialize(
            new BridgeCommand
            {
                CommandType = BridgeCommandTypes.Reflect,
                Args = ["methods", "ConduitReflectDerivedFixture", "GenericMethod"],
            },
            ConduitJsonContext.Default.BridgeCommand
        );

        await Assert.That(payload).Contains("\"command_type\":\"reflect\"");
        await Assert.That(payload).Contains("\"args\":[\"methods\",\"ConduitReflectDerivedFixture\",\"GenericMethod\"]");
    }

    [Test]
    public async Task ProfilerToolEnumsSerializeAsWireNames()
    {
        var action = JsonSerializer.Serialize(ProfilerRecordAction.Capture, ConduitJsonContext.Default.ProfilerRecordAction);
        var overviewMode = JsonSerializer.Serialize(ProfilerOverviewMode.GcKb, ConduitJsonContext.Default.ProfilerOverviewMode);
        var browseSort = JsonSerializer.Serialize(ProfilerBrowseSort.SelfMs, ConduitJsonContext.Default.ProfilerBrowseSort);

        await Assert.That(action).IsEqualTo("\"capture\"");
        await Assert.That(overviewMode).IsEqualTo("\"gc_kb\"");
        await Assert.That(browseSort).IsEqualTo("\"self_ms\"");
    }

    [Test]
    public async Task McpToolParameterTypesHaveSourceGeneratedJsonMetadata()
    {
        var missing = typeof(UnityTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttributes(inherit: false).Any(attribute => attribute.GetType().Name == "McpServerToolAttribute"))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType)
            .Where(type => type != typeof(UnityProjectOperations)
                           && type != typeof(UnityProjectRegistry)
                           && type != typeof(CancellationToken))
            .Distinct()
            .Where(type => ConduitJsonContext.Default.GetTypeInfo(type) == null)
            .Select(type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert.That(string.Join("\n", missing)).IsEqualTo("");
    }
}
