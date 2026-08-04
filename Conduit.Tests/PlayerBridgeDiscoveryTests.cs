using System.Text.Json;

namespace Conduit;

public sealed class PlayerBridgeDiscoveryTests
{
    [Test]
    public async Task PlayerSelectorRequiresTheExactCanonicalForm()
    {
        await Assert.That(PlayerSelector.TryParse("player:3910489", out var selector)).IsTrue();
        await Assert.That(selector.ProcessId).IsEqualTo(3910489);
        await Assert.That(selector.ToString()).IsEqualTo("player:3910489");

        foreach (var invalid in new[]
                 {
                     " player:3910489",
                     "Player:3910489",
                     "player:0",
                     "player:-1",
                     "player:3910489/",
                     "/tmp/player:3910489",
                 })
            await Assert.That(PlayerSelector.TryParse(invalid, out _)).IsFalse();
    }

    [Test]
    public async Task MalformedPlayerSelectorDoesNotFallThroughToAProjectPath()
    {
        await Assert.That(() => BridgeTarget.Normalize("player:not-a-process"))
            .Throws<ArgumentException>()
            .WithMessage("Player selector 'player:not-a-process' is malformed; player process IDs are positive integers.");
    }

    [Test]
    public async Task DiscoveryRejectsStaleMalformedAndNonPlayerEndpoints()
    {
        var now = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
        var root = CreateTemporaryDirectory();
        try
        {
            var live = Endpoint(101, "live", now);
            live.IsTestPlayer = true;
            WriteEndpoint(root, "live", live);
            WriteEndpoint(root, "stale", Endpoint(102, "stale", now - TimeSpan.FromSeconds(11)));
            var editor = Endpoint(103, "editor", now);
            editor.EndpointKind = BridgeEndpointKinds.Editor;
            WriteEndpoint(
                root,
                "editor",
                editor
            );
            var futureProtocol = Endpoint(104, "future", now);
            futureProtocol.ProtocolVersion = BridgeProtocol.Version + 1;
            WriteEndpoint(
                root,
                "future-protocol",
                futureProtocol
            );
            var editorRuntime = Endpoint(105, "editor-runtime", now);
            editorRuntime.Platform = "LinuxEditor";
            WriteEndpoint(root, "editor-runtime", editorRuntime);
            var malformedDirectory = Path.Combine(root, "endpoints", "malformed");
            Directory.CreateDirectory(malformedDirectory);
            File.WriteAllText(Path.Combine(malformedDirectory, "endpoint.json"), "{");

            var discovery = new UnityPlayerDiscovery(
                new FakeTimeProvider(now),
                () => [root]
            );
            var endpoints = discovery.Discover();

            await Assert.That(endpoints.Length).IsEqualTo(1);
            await Assert.That(endpoints[0].ProcessId).IsEqualTo(101);
            await Assert.That(endpoints[0].IsTestPlayer).IsTrue();
            await Assert.That(endpoints[0].EndpointDirectoryPath)
                .IsEqualTo(Path.Combine(root, "endpoints", "live"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ReusedProcessIdIsReportedAsAmbiguous()
    {
        var now = DateTimeOffset.UtcNow;
        var root = CreateTemporaryDirectory();
        try
        {
            WriteEndpoint(root, "first", Endpoint(3910489, "first", now));
            WriteEndpoint(root, "second", Endpoint(3910489, "second", now));
            var discovery = new UnityPlayerDiscovery(
                new FakeTimeProvider(now),
                () => [root]
            );

            var resolution = discovery.Resolve(new(3910489));

            await Assert.That(resolution.Endpoint).IsNull();
            await Assert.That(resolution.IsAmbiguous).IsTrue();
            await Assert.That(resolution.Diagnostic).Contains("2 live player sessions");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ResolutionRetriesATransientlyMissingEndpoint()
    {
        var now = new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        var root = CreateTemporaryDirectory();
        try
        {
            var discovery = new UnityPlayerDiscovery(time, () => [root]);
            var resolutionTask = discovery.ResolveAsync(new(3910489), CancellationToken.None);

            await Assert.That(resolutionTask.IsCompleted).IsFalse();
            WriteEndpoint(root, "appeared", Endpoint(3910489, "appeared", now));
            time.Advance(UnityPlayerDiscovery.ResolutionRetryDelay);

            var resolution = await resolutionTask;
            await Assert.That(resolution.Endpoint?.ProcessId).IsEqualTo(3910489);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task FallbackIdentityAssociatesEveryMatchingLocalProjectCopy()
    {
        var first = CreateProject("TryFinally", "Sample", "");
        var second = CreateProject("TryFinally", "Sample", "");
        try
        {
            var player = Endpoint(200, "player", DateTimeOffset.UtcNow);
            player.CompanyName = "TryFinally";
            player.ProductName = "Sample";

            await Assert.That(UnityProjectIdentity.Read(first).Matches(player)).IsTrue();
            await Assert.That(UnityProjectIdentity.Read(second).Matches(player)).IsTrue();
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Test]
    public async Task CloudIdentityTakesPrecedenceOverCompanyAndProduct()
    {
        var matching = CreateProject("Other", "Other", "AABBCC");
        var fallbackOnly = CreateProject("TryFinally", "Sample", "");
        try
        {
            var player = Endpoint(200, "player", DateTimeOffset.UtcNow);
            player.CloudProjectId = "aabbcc";
            player.CompanyName = "TryFinally";
            player.ProductName = "Sample";

            await Assert.That(UnityProjectIdentity.Read(matching).Matches(player)).IsTrue();
            await Assert.That(UnityProjectIdentity.Read(fallbackOnly).Matches(player)).IsFalse();
        }
        finally
        {
            Directory.Delete(matching, recursive: true);
            Directory.Delete(fallbackOnly, recursive: true);
        }
    }

    [Test]
    public async Task InMemoryArtifactRoundTripsAndRejectsTampering()
    {
        var bytes = Enumerable.Range(0, 200_000)
            .Select(static value => (byte)(value % 251))
            .ToArray();
        var artifact = BridgeArtifact.FromBytes(
            "snippet.dll",
            "application/vnd.microsoft.portable-executable",
            bytes
        );

        await Assert.That(artifact.Decode()).IsEquivalentTo(bytes);

        artifact.Content![0] ^= 1;
        await Assert.That(artifact.Decode).Throws<InvalidDataException>();
    }

    static BridgeEndpointDescriptor Endpoint(
        int processId,
        string sessionId,
        DateTimeOffset lastSeen) =>
        new()
        {
            EndpointKind = BridgeEndpointKinds.Player,
            Transport = BridgeTransportKinds.Fifo,
            EndpointId = $"player-{processId}-{sessionId}",
            ProcessId = processId,
            SessionInstanceId = sessionId,
            LastSeenUtc = lastSeen.ToString("O"),
        };

    static void WriteEndpoint(
        string root,
        string directoryName,
        BridgeEndpointDescriptor endpoint)
    {
        var directory = Path.Combine(root, "endpoints", directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "endpoint.json"),
            JsonSerializer.Serialize(
                endpoint,
                ConduitJsonContext.Default.BridgeEndpointDescriptor
            )
        );
    }

    static string CreateProject(
        string companyName,
        string productName,
        string cloudProjectId)
    {
        var project = CreateTemporaryDirectory();
        var settingsDirectory = Path.Combine(project, "ProjectSettings");
        Directory.CreateDirectory(settingsDirectory);
        File.WriteAllText(
            Path.Combine(settingsDirectory, "ProjectSettings.asset"),
            $"""
             PlayerSettings:
               companyName: {companyName}
               productName: {productName}
               cloudProjectId: {cloudProjectId}
             """
        );
        return project;
    }

    static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "conduit-player-tests-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
