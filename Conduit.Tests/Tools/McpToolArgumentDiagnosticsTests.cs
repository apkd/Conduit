using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Conduit;

public sealed class McpToolArgumentDiagnosticsTests
{
    [Test]
    public async Task InvalidArgumentsReturnSpecificDiagnostics()
    {
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(
                new()
                {
                    Command = "dotnet",
                    Arguments = [typeof(UnityTools).Assembly.Location],
                    Name = "Conduit argument diagnostic test",
                }
            )
        );

        await AssertError(
            BridgeCommandTypes.ReimportAssets,
            new() { ["projectPath"] = "/tmp/project", ["path"] = "Assets/Example.asset" },
            "missing required argument 'query'; "
                + "unknown argument 'path' (expected arguments: 'projectPath', 'query')"
        );
        await AssertError(
            BridgeCommandTypes.FromJsonOverwrite,
            new(),
            "missing required argument 'projectPath'; missing required argument 'query'; "
                + "missing required argument 'json'"
        );
        await AssertError(
            BridgeCommandTypes.ReimportAssets,
            new() { ["projectPath"] = "/tmp/project", ["query"] = 42 },
            "argument 'query' must be a string, but received a number"
        );
        await AssertError(
            BridgeCommandTypes.Status,
            new() { ["projectPath"] = null },
            "argument 'projectPath' cannot be null; expected a string"
        );
        await AssertError(
            BridgeCommandTypes.RunTestsEditMode,
            new() { ["projectPath"] = "/tmp/project", ["async"] = "yes" },
            "argument 'async' must be a boolean, but received a string"
        );
        await AssertError(
            BridgeCommandTypes.ProfilerRecord,
            new() { ["projectPath"] = "/tmp/project", ["action"] = "bogus" },
            "argument 'action' must be one of 'capture', 'save', 'load', 'list'"
        );
        await AssertError(
            BridgeCommandTypes.ProfilerRecord,
            new() { ["projectPath"] = "/tmp/project", ["frames"] = 1.5 },
            "argument 'frames' must be a 32-bit integer"
        );
        await AssertError(
            BridgeCommandTypes.ProfilerRecord,
            new() { ["projectPath"] = "/tmp/project", ["frames"] = long.MaxValue },
            "argument 'frames' must be a 32-bit integer"
        );
        using var oversizedNumber = JsonDocument.Parse("1e400");
        await AssertError(
            BridgeCommandTypes.ProfilerRecord,
            new()
            {
                ["projectPath"] = "/tmp/project",
                ["delaySeconds"] = oversizedNumber.RootElement.Clone(),
            },
            "argument 'delaySeconds' must be a finite number"
        );
        await AssertError(
            "help",
            new() { ["unexpected"] = true },
            "unknown argument 'unexpected' (expected arguments: 'projectPath')"
        );

        var validNull = await client.CallToolAsync(
            BridgeCommandTypes.SaveScenes,
            new Dictionary<string, object?> { ["projectPath"] = "/tmp/project", ["scenePath"] = null }
        );
        await Assert.That(validNull.IsError == true).IsFalse();

        var validRestartOptions = await client.CallToolAsync(
            BridgeCommandTypes.Restart,
            new Dictionary<string, object?>
            {
                ["projectPath"] = Path.Combine(Path.GetTempPath(), $"conduit-invalid-project-{Guid.NewGuid():N}"),
                ["editorArguments"] = new[] { "-diagnostic-flag" },
                ["environmentVariables"] = new Dictionary<string, string?>
                {
                    ["REMOVED_VARIABLE"] = null,
                },
            }
        );
        await Assert.That(validRestartOptions.IsError == true).IsFalse();

        async Task AssertError(string tool, Dictionary<string, object?> arguments, string diagnostic)
        {
            var result = await client.CallToolAsync(tool, arguments);

            await Assert.That(result.IsError).IsTrue();
            await Assert.That(result.Content).HasSingleItem();
            await Assert.That(((TextContentBlock)result.Content[0]).Text).IsEqualTo(
                $"An error occurred invoking '{tool}': Invalid arguments for '{tool}': {diagnostic}."
            );
        }
    }
}
