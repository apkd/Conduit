using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Conduit;

public sealed class McpToolArgumentDiagnosticsTests
{
    [Test]
    public async Task MissingRequiredArgumentReturnsSpecificDiagnostic()
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
        var result = await client.CallToolAsync(
            BridgeCommandTypes.ReimportAssets,
            new Dictionary<string, object?>
            {
                ["projectPath"] = "/tmp/project",
                ["path"] = "Assets/Example.asset",
            }
        );

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Content).HasSingleItem();
        await Assert.That(((TextContentBlock)result.Content[0]).Text).IsEqualTo(
            "An error occurred invoking 'reimport_assets': " +
            "Invalid arguments for 'reimport_assets': missing required argument 'query'."
        );
    }
}
