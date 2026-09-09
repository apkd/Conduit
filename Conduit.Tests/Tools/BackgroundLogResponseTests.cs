namespace Conduit;

public sealed class BackgroundLogResponseTests
{
    [Test]
    public async Task PreservesInlineOutputAndAppendsBackgroundSeparately()
    {
        var result = new BridgeCommandResult
        {
            ReturnValue = "answer",
            Logs = "inline details",
            BackgroundLogs = "earlier failure",
            DisplayName = "snippet.cs",
        };
        var roundTrip = BridgeProtocol.Deserialize(BridgeProtocol.Serialize(
            BridgeMessage.CreateCommandResult("request", result)
        ))!;
        var text = ToolResponseFormatter.Format(roundTrip.Result!.ToToolExecutionResult());
        await Assert.That(text.Contains(result.ReturnValue, StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains(result.DisplayName, StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.Contains(result.Logs, StringComparison.Ordinal)).IsTrue();
        await Assert.That(text.IndexOf(result.BackgroundLogs, StringComparison.Ordinal))
            .IsGreaterThan(text.IndexOf(result.Logs, StringComparison.Ordinal));
    }

    [Test]
    public async Task BackgroundEligibilityRoundTripsIndependentlyOfUsageTracking()
    {
        var message = BridgeMessage.CreateCommand("request", new()
        {
            CommandType = BridgeCommandTypes.Status,
            IncludeBackgroundLogs = true,
            TrackUsage = false,
        });
        var command = BridgeProtocol.Deserialize(BridgeProtocol.Serialize(message))!.Command!;
        await Assert.That(command.IncludeBackgroundLogs).IsTrue();
        await Assert.That(command.TrackUsage).IsNull();
    }

    [Test]
    public async Task MissingBackgroundDoesNotChangeExistingResponse()
    {
        const string content = "existing status report";
        await Assert.That(ToolResponseFormatter.AppendBackgroundLogs(content, null)).IsEqualTo(content);
    }
}
