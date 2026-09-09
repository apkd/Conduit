using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Conduit;

public sealed class EditorIdleCloseTests
{
    [Test]
    public async Task IdleCloseBlocksToolsBeforeExecutionAndSurvivesServerRestart()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), $"conduit-idle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        await File.WriteAllTextAsync(Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1");
        BridgeIdleCloseMarker.Write(projectPath);
        try
        {
            await using (var client = await StartServer())
            {
                await AssertBlocked(client, "status", new());
                await AssertBlocked(client, "show", new() { ["query"] = "*" });
                await AssertBlocked(client, "execute_code", new() { ["snippet"] = "this is not C#" });
                await AssertBlocked(client, "detour", new() { ["methodName"] = "missing", ["replacementBody"] = "this is not C#" });
                await AssertBlocked(client, "help", new());

                var help = await client.CallToolAsync("help", new Dictionary<string, object?>());
                await Assert.That(GetText(help)).IsNotEqualTo(BridgeIdleCloseMarker.Diagnostic);

                // the missing package stops restart before launching anything, after it passes the filter
                var restart = await client.CallToolAsync("restart", new Dictionary<string, object?> { ["projectPath"] = projectPath });
                await Assert.That(GetText(restart)).Contains(UnityProjectOfflinePreflight.MissingPackageDiagnostic);
                await Assert.That(BridgeIdleCloseMarker.Exists(projectPath)).IsTrue();
            }

            await using (var client = await StartServer())
            {
                await AssertBlocked(client, "status", new());
                BridgeIdleCloseMarker.Clear(projectPath);
                var help = await client.CallToolAsync("help", new Dictionary<string, object?> { ["projectPath"] = projectPath });
                await Assert.That(GetText(help)).IsNotEqualTo(BridgeIdleCloseMarker.Diagnostic);
            }
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }

        async Task AssertBlocked(McpClient client, string tool, Dictionary<string, object?> arguments)
        {
            arguments["projectPath"] = projectPath;
            var result = await client.CallToolAsync(tool, arguments);
            await Assert.That(GetText(result)).IsEqualTo(BridgeIdleCloseMarker.Diagnostic);
        }

        static Task<McpClient> StartServer() => McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = [typeof(UnityTools).Assembly.Location],
            Name = "Conduit idle-close test",
        }));

        static string GetText(CallToolResult result) => ((TextContentBlock)result.Content.Single()).Text;
    }

    [Test]
    [Arguments(false, false, false, false)]
    [Arguments(true, false, false, true)]
    [Arguments(true, true, false, false)]
    [Arguments(true, false, true, false)]
    public async Task IdleMarkerOnlyDescribesAnOfflineEditor(bool marker, bool processRunning, bool locked, bool expected)
    {
        var snapshot = new UnityProjectEnvironmentSnapshot(
            "/project", true, null,
            locked ? UnityProjectLockfileState.Locked : UnityProjectLockfileState.Missing,
            processRunning ? 1 : 0,
            processRunning ? new(123, "Unity", "Unity -projectPath /project") : null
        ) { HasIdleCloseMarker = marker };

        await Assert.That(snapshot.WasClosedAfterIdle).IsEqualTo(expected);
        if (expected)
        {
            var failure = BridgeClientResult.Failure(null, BridgeRuntimeFailureKind.ProcessExited, "process exited", false);
            string? diagnostic = UnityProjectOfflinePreflight.ResolveBlockedDiagnostic(snapshot, failure, null, true);
            await Assert.That(diagnostic).IsEqualTo(BridgeIdleCloseMarker.Diagnostic);
            await Assert.That(UnityStatusPolicy.ShouldWaitForBlockedStatusProgressWindow(snapshot, diagnostic!, failure)).IsFalse();
        }
    }
}
