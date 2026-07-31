using JetBrains.Annotations;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
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
            await Assert.That(materialized[0].Chunks).IsEmpty();
            await Assert.That(materialized[0].RelativePath)
                .IsEqualTo($"endpoints/player-session/artifacts/{source.Sha256}.dll");
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
}
