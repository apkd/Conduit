#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
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
    [TestCase(@"Y:\host", @"Z:\legacy", @"Y:\host")]
    [TestCase(null, @"Z:\legacy", @"Z:\legacy")]
    public void WineIpcRootUsesHostHome(string? hostHome, string legacyHome, string expectedHome)
    {
        var previousRoot = Environment.GetEnvironmentVariable("CONDUIT_IPC_ROOT");
        var previousHostHome = Environment.GetEnvironmentVariable("WINE_HOST_HOME");
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            Environment.SetEnvironmentVariable("CONDUIT_IPC_ROOT", null);
            Environment.SetEnvironmentVariable("WINE_HOST_HOME", hostHome);
            Environment.SetEnvironmentVariable("HOME", legacyHome);

            Assert.That(
                RuntimeIpcPaths.GetRoot(wine: true),
                Is.EqualTo(
                    Path.Combine(
                        expectedHome,
                        ".local",
                        "state",
                        "conduit",
                        "ipc",
                        "v1"
                    )
                )
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONDUIT_IPC_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("WINE_HOST_HOME", previousHostHome);
            Environment.SetEnvironmentVariable("HOME", previousHome);
        }
    }

    [Test]
    public void RuntimeBridgeOnlyStartsInAPlayer()
    {
        var bridge = Resources.FindObjectsOfTypeAll<RuntimeBridgeBehaviour>()
            .FirstOrDefault();

        Assert.That(bridge == null, Is.EqualTo(Application.isEditor));
    }

    [Test]
    public void ArtifactTransferRejectsModifiedContent()
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
        artifact.Content![0] ^= 1;
        Assert.Throws<InvalidDataException>(() => artifact.Decode());
    }

    [Test]
    public void ArtifactTransferReadsAndVerifiesSharedSessionFile()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "conduit-runtime-artifact-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var endpointDirectory = Path.Combine(root, "endpoints", "player-session");
            var bytes = new byte[] { 1, 2, 3, 4 };
            var produced = BridgeArtifact.FromBytes(
                "snippet.dll",
                "application/vnd.microsoft.portable-executable",
                bytes
            );
            produced.MaterializeInEndpoint(endpointDirectory);
            var artifact = new BridgeArtifact
            {
                name = produced.name,
                media_type = produced.media_type,
                sha256 = produced.sha256,
                length = produced.length,
                relative_path = produced.relative_path,
            };
            artifact.ResolveInEndpoint(endpointDirectory);

            Assert.That(artifact.Decode(), Is.EqualTo(bytes));
            artifact.relative_path = "artifacts/../snippet.dll";
            Assert.Throws<InvalidDataException>(
                () => artifact.ResolveInEndpoint(endpointDirectory)
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AssemblyReferenceTransferReturnsRequestedArtifactsInOrder()
    {
        var assemblies = new[]
        {
            typeof(RuntimeBridgeTests).Assembly,
            typeof(GameObject).Assembly,
        };
        var ids = assemblies
            .Select(static assembly => assembly.ManifestModule.ModuleVersionId.ToString("N"))
            .ToArray();

        var result = AssemblyReferences.GetAssemblyBlobs(ids);

        Assert.That(result.outcome, Is.EqualTo(ToolOutcome.Success));
        Assert.That(
            result.artifacts.Select(static artifact => artifact.name),
            Is.EqualTo(ids.Select(static id => id + ".dll"))
        );
        Assert.That(result.artifacts.All(static artifact => artifact.Content != null), Is.True);
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
    public void SearchResultsIdentifyGameObjectsAndComponentTypes()
    {
        var gameObject = new GameObject("Conduit Runtime Typed Match");
        try
        {
            var matches = ConduitRuntimeSearch.ResolveMany("Conduit Runtime Typed Match");
            var formatted = ConduitRuntimeSearch.FormatMatches(matches, includeHint: false);

            Assert.That(formatted, Does.Contain("Conduit Runtime Typed Match (GameObject)"));
            Assert.That(formatted, Does.Contain("Conduit Runtime Typed Match (Transform)"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void ExactIdResolvesHiddenRuntimeObject()
    {
        var gameObject = new GameObject("Conduit Hidden Runtime Object")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        try
        {
            var matches = ConduitRuntimeSearch.ResolveMany(BridgeObjectId.Format(gameObject));

            Assert.That(matches, Is.EqualTo(new UnityEngine.Object[] { gameObject }));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void RuntimeSearchReportsTruncatedResults()
    {
        var objects = new List<GameObject>();
        try
        {
            for (var index = 0; index < 30; index++)
                objects.Add(new GameObject("Conduit Runtime Truncation"));

            var matches = ConduitRuntimeSearch.ResolveMany("Conduit Runtime Truncation");
            var output = ConduitRuntimeSearch.FormatMatches(matches, includeHint: false);

            Assert.That(matches.Count, Is.GreaterThanOrEqualTo(30));
            Assert.That(output, Does.Contain("Showing the first 25 results; additional matches were omitted."));
        }
        finally
        {
            foreach (var gameObject in objects)
                UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void RuntimeTransformPatchPreservesOmittedVectorMembers()
    {
        var gameObject = new GameObject("Conduit Runtime Transform Patch");
        try
        {
            gameObject.transform.localPosition = new(0f, 1f, -10f);

            var result = RuntimeObjectJsonUtility.FromJsonOverwrite(
                gameObject.transform,
                "{\"Transform\":{\"localPosition\":{\"x\":5.0}}}"
            );

            Assert.That(gameObject.transform.localPosition, Is.EqualTo(new Vector3(5f, 1f, -10f)));
            Assert.That(result, Is.EqualTo("Applied changes:\n- localPosition.x"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public void RuntimePropertyPatchValidatesBeforeMutationAndRejectsUnknownMembers()
    {
        var gameObject = new GameObject("Conduit Runtime Camera Patch");
        var camera = gameObject.AddComponent<Camera>();
        try
        {
            camera.fieldOfView = 62f;
            camera.depth = -1f;

            Assert.Throws<InvalidOperationException>(() => RuntimeObjectJsonUtility.FromJsonOverwrite(
                camera,
                "{\"Camera\":{\"depth\":4.0,\"fieldOfView\":\"bad\"}}"
            ));
            Assert.That(camera.depth, Is.EqualTo(-1f));

            Assert.Throws<InvalidOperationException>(() => RuntimeObjectJsonUtility.FromJsonOverwrite(
                camera,
                "{\"Camera\":{\"fieldOfView\":63.0,\"definitelyMissing\":1}}"
            ));
            Assert.That(camera.fieldOfView, Is.EqualTo(62f));

            var result = RuntimeObjectJsonUtility.FromJsonOverwrite(
                camera,
                "{\"Camera\":{\"fieldOfView\":61.0}}"
            );
            Assert.That(result, Is.EqualTo("Applied changes:\n- fieldOfView"));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    [Test]
    public async Task RuntimeShowIncludesComponentsAndWritableProperties()
    {
        var gameObject = new GameObject("Conduit Runtime Show State");
        var camera = gameObject.AddComponent<Camera>();
        try
        {
            var result = await RuntimeToolDispatcher.ExecuteAsync(
                new BridgeCommand
                {
                    command_type = BridgeCommandTypes.Show,
                    target = BridgeObjectId.Format(gameObject),
                },
                CancellationToken.None
            );

            Assert.That(result.return_value, Does.Contain("Components:"));
            Assert.That(result.return_value, Does.Contain(typeof(Camera).FullName));
            Assert.That(result.return_value, Does.Contain(BridgeObjectId.Format(camera)));
            Assert.That(result.return_value, Does.Contain("Properties:"));
            Assert.That(result.return_value, Does.Contain("activeSelf"));
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
