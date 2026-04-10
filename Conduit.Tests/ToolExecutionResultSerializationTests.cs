using System.Text.Json;
using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ToolExecutionResultSerializationTests
{
    [Test]
    public async Task NormalizeUserFacingTextCanonicalizesCarriageReturns()
    {
        var normalized = ConduitUtility.NormalizeUserFacingText("a\r\nb\rc");

        await Assert.That(normalized).IsEqualTo("a\nb\nc");
    }

    [Test]
    public async Task BridgeResultDropsEmptyOptionalFields()
    {
        var result = new BridgeCommandResult
        {
            Outcome = ToolOutcome.Success,
            Logs = string.Empty,
            ReturnValue = string.Empty,
            Diagnostic = string.Empty,
            Exception = new()
            {
                Type = string.Empty,
                Message = string.Empty,
            },
        }.ToToolExecutionResult();

        await Assert.That(result.Logs).IsNull();
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
    public async Task SerializedToolResultKeepsMeaningfulOptionalFields()
    {
        var payload = JsonSerializer.Serialize(
            new()
            {
                Outcome = ToolOutcome.Exception,
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
}
