namespace Conduit;

public sealed class PlayerArtifactStoreTests
{
    [Test]
    public async Task MaterializesArtifactInsidePlayerEndpoint()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "conduit-artifact-store-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var endpointDirectory = Path.Combine(root, "endpoints", "player-session");
            var endpoint = new BridgeEndpointDescriptor
            {
                EndpointId = "player-session",
                EndpointDirectoryPath = endpointDirectory,
            };
            var bytes = new byte[] { 1, 2, 3, 4 };
            var source = BridgeArtifact.FromBytes(
                "snippet.dll",
                "application/vnd.microsoft.portable-executable",
                bytes
            );

            var materialized = PlayerArtifactStore.Materialize(endpoint, [source]);

            await Assert.That(materialized.Length).IsEqualTo(1);
            await Assert.That(materialized[0].Content).IsNull();
            await Assert.That(materialized[0].Length).IsEqualTo(bytes.LongLength);
            await Assert.That(materialized[0].RelativePath)
                .IsEqualTo($"artifacts/{source.Sha256}.dll");
            await Assert.That(
                File.ReadAllBytes(
                    Path.Combine(
                        endpointDirectory,
                        "artifacts",
                        source.Sha256 + ".dll"
                    )
                )
            ).IsEquivalentTo(bytes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task EndpointArtifactResolutionRejectsTraversalAndTampering()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "conduit-artifact-security-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var endpointDirectory = Path.Combine(root, "endpoints", "player-session");
            var endpoint = new BridgeEndpointDescriptor
            {
                EndpointDirectoryPath = endpointDirectory,
            };
            var bytes = new byte[] { 1, 2, 3, 4 };
            var materialized = PlayerArtifactStore.Materialize(
                endpoint,
                [BridgeArtifact.FromBytes("result.png", "image/png", bytes)]
            )[0];
            var received = new BridgeArtifact
            {
                Name = materialized.Name,
                MediaType = materialized.MediaType,
                Sha256 = materialized.Sha256,
                Length = materialized.Length,
                RelativePath = materialized.RelativePath,
            };

            received.ResolveInEndpoint(endpointDirectory);
            await Assert.That(received.Decode()).IsEquivalentTo(bytes);
            File.WriteAllBytes(received.ResolvedPath!, new byte[] { 9, 2, 3, 4 });
            await Assert.That(received.Decode).Throws<InvalidDataException>();

            received.RelativePath = "artifacts/../escaped.dll";
            await Assert.That(() => received.ResolveInEndpoint(endpointDirectory))
                .Throws<InvalidDataException>();
            received.RelativePath = Path.GetFullPath(Path.Combine(root, "escaped.dll"));
            await Assert.That(() => received.ResolveInEndpoint(endpointDirectory))
                .Throws<InvalidDataException>();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
