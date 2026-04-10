using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class ToolResponseFormatterTests
{
    [Test]
    public async Task SuccessWithOnlyReturnValueReturnsRawPayload()
    {
        var text = ToolResponseFormatter.Format(
            new()
            {
                Outcome = ToolOutcome.Success,
                ReturnValue = "hello",
            }
        );

        await Assert.That(text).IsEqualTo("hello");
    }

    [Test]
    public async Task SuccessWithReturnValueAndLogsUsesSections()
    {
        var text = ToolResponseFormatter.Format(
            new()
            {
                Outcome = ToolOutcome.Success,
                ReturnValue = "42",
                Logs = "> probe\nUnityEngine.Debug:Log",
            }
        );

        await Assert.That(text).IsEqualTo("RESULT:\n42\n\nLOG:\n> probe\nUnityEngine.Debug:Log");
    }

    [Test]
    public async Task FailureWithOnlyDiagnosticReturnsRawDiagnostic()
    {
        var text = ToolResponseFormatter.Format(
            new()
            {
                Outcome = ToolOutcome.NotConnected,
                Diagnostic = "Unity editor is offline for this project.",
            }
        );

        await Assert.That(text).IsEqualTo("Unity editor is offline for this project.");
    }

    [Test]
    public async Task ExceptionFormatsTypeMessageAndStackTrace()
    {
        var text = ToolResponseFormatter.Format(
            new()
            {
                Outcome = ToolOutcome.Exception,
                Exception = new()
                {
                    Type = "InvalidOperationException",
                    Message = "boom",
                    StackTrace = "frame 1\nframe 2",
                },
            }
        );

        await Assert.That(text).IsEqualTo("InvalidOperationException: boom\n\nStack Trace:\nframe 1\nframe 2");
    }

    [Test]
    public async Task FailureWithMultipleFieldsUsesLabeledSections()
    {
        var text = ToolResponseFormatter.Format(
            new()
            {
                Outcome = ToolOutcome.Exception,
                Diagnostic = "Command failed.",
                Logs = "captured log",
                Exception = new()
                {
                    Type = "InvalidOperationException",
                    Message = "boom",
                },
            }
        );

        await Assert.That(text)
            .IsEqualTo(
                "DIAGNOSTIC:\nCommand failed.\n\nEXCEPTION:\nInvalidOperationException: boom\n\nLOG:\ncaptured log"
            );
    }

    [Test]
    public async Task EmptyPayloadFallsBackToOutcomeText()
    {
        var text = ToolResponseFormatter.Format(
            new()
            {
                Outcome = ToolOutcome.Timeout,
            }
        );

        await Assert.That(text).IsEqualTo("Timeout");
    }
}
