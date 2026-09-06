#nullable enable

#if UNITY_EDITOR
using System.Threading.Tasks;
using Conduit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed partial class ConduitMcpEndToEndTests
{
    [Test]
    public async Task BackgroundLogs_StatusConsumesOnceAndInlineLogsStaySeparate()
    {
        var settings = ConduitSettings.instance;
        var enabled = settings.IncludeBackgroundLogs;
        try
        {
            settings.SetIncludeBackgroundLogs(true);
            BridgeLogs.TakeBackground();
            const string background = "Conduit between-call root cause";
            LogAssert.Expect(LogType.Error, background);
            Debug.LogError(background);

            var status = await client.CallToolAsync(BridgeCommandTypes.Status, Args(("projectPath", projectPath)));
            Assert.That(status.Text, Does.Contain(background));
            var next = await client.CallToolAsync(BridgeCommandTypes.Status, Args(("projectPath", projectPath)));
            Assert.That(next.Text, Does.Not.Contain(background));

            const string inline = "Conduit inline probe";
            var execution = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", "Debug.Log(\"" + inline + "\"); return 42;"))
            );
            Assert.That(execution.Text, Does.Contain(inline));
            var after = await client.CallToolAsync(BridgeCommandTypes.Status, Args(("projectPath", projectPath)));
            Assert.That(after.Text, Does.Not.Contain(inline));
        }
        finally
        {
            settings.SetIncludeBackgroundLogs(false);
            settings.SetIncludeBackgroundLogs(enabled);
        }
    }

    [Test]
    public async Task BackgroundLogs_ServerFailuresAndCompilationHelpersLeaveSummaryForExecution()
    {
        var settings = ConduitSettings.instance;
        var enabled = settings.IncludeBackgroundLogs;
        try
        {
            settings.SetIncludeBackgroundLogs(true);
            BridgeLogs.TakeBackground();
            const string background = "Conduit pending compilation diagnostic";
            Debug.Log(background);

            var failure = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", "return missingConduitVariable;"))
            );
            Assert.That(failure.Text, Does.Not.Contain(background));

            var execution = await client.CallToolAsync(
                BridgeCommandTypes.ExecuteCode,
                Args(("projectPath", projectPath), ("snippet", "return 42;"))
            );
            Assert.That(execution.Text, Does.Contain(background));
            var after = await client.CallToolAsync(BridgeCommandTypes.Status, Args(("projectPath", projectPath)));
            Assert.That(after.Text, Does.Not.Contain(background));
        }
        finally
        {
            settings.SetIncludeBackgroundLogs(false);
            settings.SetIncludeBackgroundLogs(enabled);
        }
    }

    [Test]
    public async Task BackgroundLogs_DisablingClearsPendingMessages()
    {
        var settings = ConduitSettings.instance;
        var enabled = settings.IncludeBackgroundLogs;
        try
        {
            settings.SetIncludeBackgroundLogs(true);
            Debug.Log("Conduit discarded background");
            settings.SetIncludeBackgroundLogs(false);
            settings.SetIncludeBackgroundLogs(true);
            var status = await client.CallToolAsync(BridgeCommandTypes.Status, Args(("projectPath", projectPath)));
            Assert.That(status.Text, Does.Not.Contain("Conduit discarded background"));
        }
        finally
        {
            settings.SetIncludeBackgroundLogs(false);
            settings.SetIncludeBackgroundLogs(enabled);
        }
    }
}
#endif
