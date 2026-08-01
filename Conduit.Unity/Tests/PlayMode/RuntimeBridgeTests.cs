#nullable enable

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Conduit;
using Conduit.Runtime;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class RuntimeBridgeTests
{
    [Test]
    public void RuntimeBridgeOnlyStartsInAPlayer()
    {
        var bridge = Resources.FindObjectsOfTypeAll<RuntimeBridgeBehaviour>()
            .FirstOrDefault();

        Assert.That(bridge == null, Is.EqualTo(Application.isEditor));
    }

    [Test]
    public void ArtifactTransferRejectsModifiedChunks()
    {
        var bytes = Enumerable.Range(0, 100_000)
            .Select(static value => (byte)(value % 251))
            .ToArray();
        var artifact = BridgeArtifact.FromBytes(
            "snippet.dll",
            "application/vnd.microsoft.portable-executable",
            bytes
        );

        Assert.That(artifact.Decode(), Is.EqualTo(bytes));
        artifact.chunks[0] = (artifact.chunks[0][0] == 'A' ? "B" : "A")
                             + artifact.chunks[0].Substring(1);
        Assert.Throws<InvalidDataException>(() => artifact.Decode());
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
            var artifact = BridgeArtifact.FromBytes(
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
            new BridgeCommand { command_type = "playmode" },
            CancellationToken.None
        );

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Exception));
        Assert.That(
            result.diagnostic,
            Is.EqualTo("The tool `playmode` is editor-only.")
        );
    }

    [Test]
    public async Task InternalQuitCommandIsAcceptedByThePlayerDispatcher()
    {
        // an actual player invocation intentionally terminates the process and is covered by the player test run
        if (!Application.isEditor)
            return;

        var result = await RuntimeToolDispatcher.ExecuteAsync(
            new BridgeCommand { command_type = BridgeCommandTypes.QuitPlayer },
            CancellationToken.None
        );

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
    }

    [Test]
    public void SharedValueFormatterBoundsCollectionsAndDetectsCycles()
    {
        var cycle = new ArrayList();
        cycle.Add(cycle);

        Assert.That(BridgeValueFormatter.Format(cycle), Is.EqualTo("[<cycle>]"));

        var bounded = BridgeValueFormatter.Format(Enumerable.Range(0, 1025));
        Assert.That(bounded, Does.Contain("…"));
        Assert.That(bounded, Does.Not.Contain("1024"));
    }

    [Test]
    public void SharedLogCaptureDeduplicatesAndUsesCommandLogFormat()
    {
        const string message = "Conduit shared log capture";
        using var capture = new BridgeLogCapture();
        LogAssert.Expect(LogType.Warning, message);
        LogAssert.Expect(LogType.Warning, message);
        Debug.LogWarning(message);
        Debug.LogWarning(message);

        var logs = capture.Drain();

        Assert.That(logs, Does.StartWith("> " + message));
        Assert.That(logs, Does.Contain("*log repeated 2 times*"));
    }

    [Test]
    public void SharedReflectionReportPreservesPlayerFunctionPointerSignatures()
    {
        var result = reflect.Reflect(
            new[] { "methods", nameof(DetourPlayModeTests), "FunctionPointerTarget" }
        );

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(
            result.return_value,
            Does.Contain(
                "static unsafe int FunctionPointerTarget(delegate*<int, int> operation, int value)"
            )
        );
        Assert.That(result.return_value, Does.Not.Contain("// detour-incompatible"));
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

            Assert.That(gameObjectJson, Does.Contain("\"name\": \"Conduit Runtime JSON\""));
            Assert.That(cameraJson, Does.Contain("\"fieldOfView\""));

            var gameObjectChanges = RuntimeObjectJsonUtility.FromJsonOverwrite(
                gameObject,
                "{\"GameObject\":{\"name\":\"Conduit Runtime JSON Updated\"}}"
            );
            var cameraChanges = RuntimeObjectJsonUtility.FromJsonOverwrite(
                camera,
                "{\"Camera\":{\"fieldOfView\":61.0}}"
            );

            Assert.That(gameObject.name, Is.EqualTo("Conduit Runtime JSON Updated"));
            Assert.That(camera.fieldOfView, Is.EqualTo(61f));
            Assert.That(gameObjectChanges, Does.Contain("name"));
            Assert.That(cameraChanges, Does.Contain("fieldOfView"));
            Assert.That(
                RuntimeObjectJsonUtility.FromJsonOverwrite(camera, RuntimeObjectJsonUtility.ToJson(camera)),
                Is.EqualTo("No serialized properties changed.")
            );
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }
}
