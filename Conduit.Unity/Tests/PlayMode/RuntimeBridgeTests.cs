#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit.Runtime;
using NUnit.Framework;
using UnityEngine;

public sealed class RuntimeBridgeTests
{
    [Test]
    public void ArtifactTransferRejectsModifiedChunks()
    {
        var bytes = Enumerable.Range(0, 100_000)
            .Select(static value => (byte)(value % 251))
            .ToArray();
        var artifact = RuntimeBridgeArtifact.FromBytes(
            "snippet.dll",
            "application/vnd.microsoft.portable-executable",
            bytes
        );

        Assert.That(artifact.Decode(), Is.EqualTo(bytes));
        artifact.chunks[0] = (artifact.chunks[0][0] == 'A' ? "B" : "A")
                             + artifact.chunks[0].Substring(1);
        Assert.Throws<InvalidOperationException>(() => artifact.Decode());
    }

    [Test]
    public void ArtifactTransferReadsAndVerifiesSharedSessionFile()
    {
        var previousRoot = Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT");
        var root = Path.Combine(
            Path.GetTempPath(),
            "conduit-runtime-artifact-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            Environment.SetEnvironmentVariable("CONDUIT_IPC_ROOT", root);
            var relativePath = Path.Combine(
                "endpoints",
                "player-session",
                "artifacts",
                "snippet.dll"
            );
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(path, bytes);
            var artifact = RuntimeBridgeArtifact.FromBytes(
                "snippet.dll",
                "application/vnd.microsoft.portable-executable",
                bytes
            );
            artifact.relative_path = relativePath;
            artifact.chunks = Array.Empty<string>();

            Assert.That(artifact.Decode(), Is.EqualTo(bytes));
            artifact.relative_path = "../snippet.dll";
            Assert.Throws<InvalidOperationException>(() => artifact.Decode());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONDUIT_IPC_ROOT", previousRoot);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void ComponentTypeSearchReturnsOnlyMatchingGameObjects()
    {
        var gameObject = new GameObject("Conduit Runtime Camera");
        gameObject.AddComponent<Camera>();
        try
        {
            var matches = ConduitRuntimeSearch.ResolveMany(
                "Conduit Runtime Camera t:Camera"
            );

            Assert.That(matches, Has.Count.EqualTo(1));
            Assert.That(matches[0], Is.SameAs(gameObject));
            Assert.That(
                ConduitRuntimeSearch.Search<Camera>(
                    "Conduit Runtime Camera t:Camera"
                ),
                Is.SameAs(gameObject.GetComponent<Camera>())
            );
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public async Task UnsupportedCommandReturnsTheExactEditorOnlyDiagnostic()
    {
        var result = await RuntimeToolDispatcher.ExecuteAsync(
            new RuntimeBridgeCommand { command_type = "playmode" },
            CancellationToken.None
        );

        Assert.That(result.outcome, Is.EqualTo(RuntimeToolOutcome.Exception));
        Assert.That(
            result.diagnostic,
            Is.EqualTo("The tool `playmode` is editor-only.")
        );
    }

    [Test]
    public void RuntimeJsonRoundTripsGameObjectAndBuiltInComponentProperties()
    {
        var gameObject = new GameObject("Conduit Runtime JSON");
        var camera = gameObject.AddComponent<Camera>();
        try
        {
            var gameObjectJson = RuntimeObjectJsonUtility.ToJson(gameObject);
            var cameraJson = RuntimeObjectJsonUtility.ToJson(camera);

            Assert.That(gameObjectJson, Does.Contain("\"GameObject\""));
            Assert.That(gameObjectJson, Does.Contain("\"name\": \"Conduit Runtime JSON\""));
            Assert.That(cameraJson, Does.Contain("\"Camera\""));
            Assert.That(cameraJson, Does.Contain("\"fieldOfView\""));

            RuntimeObjectJsonUtility.FromJsonOverwrite(
                gameObject,
                "{\"GameObject\":{\"name\":\"Conduit Runtime JSON Updated\"}}"
            );
            RuntimeObjectJsonUtility.FromJsonOverwrite(
                camera,
                "{\"Camera\":{\"fieldOfView\":61.0}}"
            );

            Assert.That(gameObject.name, Is.EqualTo("Conduit Runtime JSON Updated"));
            Assert.That(camera.fieldOfView, Is.EqualTo(61f));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }
}
