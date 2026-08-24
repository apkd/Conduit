#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Conduit;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

public sealed partial class ConduitMcpToolsTests
{
    [Test]
    public void BridgeCommandJson_DeserializesAsyncFlag()
    {
        var command = JsonUtility.FromJson<BridgeCommand>(
            "{\"command_type\":\"run_tests_editmode\",\"async\":true}"
        );

        Assert.That(command.@async, Is.True);
    }

    [Test]
    public void RequestCancellation_UsesBridgeEnvelopeAndCancelledResult()
    {
        var message = BridgeProtocol.Deserialize(
            BridgeProtocol.Serialize(BridgeMessage.CreateCancelCommand("cancel-request"))
        );
        var result = RunTestsTool.CreateRequestCancelledResult();

        Assert.That(message, Is.Not.Null);
        Assert.That(message!.message_type, Is.EqualTo(BridgeMessageTypes.CancelCommand));
        Assert.That(message.request_id, Is.EqualTo("cancel-request"));
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Cancelled));
        Assert.That(result.diagnostic, Does.Contain("MCP request ended"));
    }

    [Test]
    public void BridgeProtocol_RoundTripsEveryEnvelopeWithOneDeserializer()
    {
        var messages = new[]
        {
            BridgeMessage.CreateHello(new() { project_path = "/tmp/project" }),
            BridgeMessage.CreateCommand(
                "command",
                new() { command_type = BridgeCommandTypes.Status }
            ),
            BridgeMessage.CreateCancelCommand("cancel"),
            BridgeMessage.CreateCommandStarted("started"),
            BridgeMessage.CreateCommandResult(
                "result",
                BridgeCommandResult.Success("done")
            ),
        };

        foreach (var expected in messages)
        {
            var actual = BridgeProtocol.Deserialize(BridgeProtocol.Serialize(expected));
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual!.message_type, Is.EqualTo(expected.message_type));
            Assert.That(actual.request_id, Is.EqualTo(expected.request_id));
            Assert.That(actual.project != null, Is.EqualTo(expected.project != null));
            Assert.That(actual.command != null, Is.EqualTo(expected.command != null));
            Assert.That(actual.result != null, Is.EqualTo(expected.result != null));
        }

        Assert.That(BridgeProtocol.Deserialize("{"), Is.Null);
        Assert.That(
            BridgeProtocol.Deserialize("{\"message_type\":\"future\"}"),
            Is.Null
        );
    }

    [Test]
    public void BridgeCompatibilityDiagnostics_AreDirectionalAndTerse()
    {
        Assert.That(
            BridgeContract.FormatProtocolMismatch(5, 4),
            Is.EqualTo("Unity Editor bridge protocol 4 is older than Conduit server protocol 5.")
        );
        Assert.That(
            BridgeContract.FormatProtocolMismatch(5, 6),
            Is.EqualTo("Conduit server protocol 5 is older than Unity Editor bridge protocol 6.")
        );

        var result = BridgeCommandResult.UnsupportedEditorTool("future_tool");
        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Exception));
        Assert.That(
            result.diagnostic,
            Is.EqualTo(
                $"Unity Editor bridge protocol {BridgeProtocol.Version} does not support the `future_tool` tool."
            )
        );
    }

    [Test]
    public async Task EditorHandshake_ReturnsItsVersionBeforeRejectingAProtocolMismatch()
    {
        var request = BridgeMessage.CreateHello(
            new BridgeProjectHandshake
            {
                project_path = ConduitProjectIdentity.GetProjectPath(),
            }
        );
        request.protocol_version = BridgeProtocol.Version - 1;
        using var input = new MemoryStream(
            System.Text.Encoding.UTF8.GetBytes(BridgeProtocol.Serialize(request) + "\n")
        );
        using var output = new MemoryStream();
        using var connection = new EditorBridgeConnection(input, output, static () => true);
        using var reader = new StreamReader(
            input,
            System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true
        );

        var accepted = await ConduitConnection.TryHandshakeAsync(
            connection,
            reader,
            System.Threading.CancellationToken.None
        );

        output.Position = 0;
        using var responseReader = new StreamReader(output, System.Text.Encoding.UTF8, false, 1024, true);
        var response = BridgeProtocol.Deserialize((await responseReader.ReadLineAsync()) ?? string.Empty);
        Assert.That(accepted, Is.False);
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.protocol_version, Is.EqualTo(BridgeProtocol.Version));
        Assert.That(response.message_type, Is.EqualTo(BridgeMessageTypes.Hello));
        Assert.That(response.project?.project_path, Is.EqualTo(ConduitProjectIdentity.GetProjectPath()));
    }

    [Test]
    public void BridgeArtifact_VerifiesProjectRelativeFilesAndRejectsTraversal()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("compiled");
        var path = Path.Combine(
            ConduitAssetPathUtility.GetProjectRootPath(),
            "Temp",
            "ConduitTests",
            "artifact.dll"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes);
        try
        {
            var artifact = BridgeArtifact.FromProjectFile(
                "artifact.dll",
                "application/octet-stream",
                "Temp/ConduitTests/artifact.dll",
                bytes
            );

            Assert.That(artifact.Decode(), Is.EqualTo(bytes));

            var traversal = BridgeArtifact.FromProjectFile(
                "artifact.dll",
                "application/octet-stream",
                "../artifact.dll",
                bytes
            );
            Assert.Throws<InvalidDataException>(() => traversal.Decode());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
