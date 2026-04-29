using System.Runtime.InteropServices;
using System.Text.Json;
using JetBrains.Annotations;
using static System.StringComparison;

namespace Conduit;

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SelfUpdaterTests
{
    [Test]
    public async Task FindAssetReturnsMatchingBrowserDownloadUrl()
    {
        using var release = JsonDocument.Parse(
            """
            {
              "tag_name": "release",
              "assets": [
                {
                  "name": "conduit-linux-x64",
                  "browser_download_url": "https://github.com/apkd/Conduit/releases/download/release/conduit-linux-x64"
                },
                {
                  "name": "conduit-win-x64.exe",
                  "browser_download_url": "https://github.com/apkd/Conduit/releases/download/release/conduit-win-x64.exe"
                }
              ]
            }
            """
        );

        var asset = SelfUpdater.FindAsset(release.RootElement, "conduit-win-x64.exe");

        await Assert.That(asset?.Name).IsEqualTo("conduit-win-x64.exe");
        await Assert.That(asset?.DownloadUrl.ToString()).EndsWith("conduit-win-x64.exe", Ordinal);
    }

    [Test]
    public async Task FindAssetIgnoresAssetNameCase()
    {
        using var release = JsonDocument.Parse(
            """
            {
              "assets": [
                {
                  "name": "conduit-linux-x64",
                  "browser_download_url": "https://github.com/apkd/Conduit/releases/download/release/conduit-linux-x64"
                }
              ]
            }
            """
        );

        var asset = SelfUpdater.FindAsset(release.RootElement, "CONDUIT-LINUX-X64");

        await Assert.That(asset?.Name).IsEqualTo("conduit-linux-x64");
    }

    [Test]
    public async Task FindAssetReturnsNullWhenAssetsAreMissing()
    {
        using var release = JsonDocument.Parse("""{ "tag_name": "release" }""");

        var asset = SelfUpdater.FindAsset(release.RootElement, "conduit-linux-x64");

        await Assert.That(asset).IsNull();
    }

    [Test]
    public async Task FindAssetThrowsWhenMatchedAssetHasInvalidDownloadUrl()
    {
        using var release = JsonDocument.Parse(
            """
            {
              "assets": [
                {
                  "name": "conduit-linux-x64",
                  "browser_download_url": "not-a-valid-url"
                }
              ]
            }
            """
        );

        await Assert.That(() => SelfUpdater.FindAsset(release.RootElement, "conduit-linux-x64"))
            .Throws<InvalidOperationException>()
            .WithMessage("Release asset 'conduit-linux-x64' does not have a valid download URL.");
    }

    [Test]
    public async Task GetReleaseAssetNameUsesPublishedAssetNames()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            await Assert.That(() => SelfUpdater.GetReleaseAssetName()).Throws<PlatformNotSupportedException>();
            return;
        }

        if (OperatingSystem.IsWindows())
            await Assert.That(SelfUpdater.GetReleaseAssetName()).IsEqualTo("conduit-win-x64.exe");
        else if (OperatingSystem.IsLinux())
            await Assert.That(SelfUpdater.GetReleaseAssetName()).IsEqualTo("conduit-linux-x64");
        else
            await Assert.That(() => SelfUpdater.GetReleaseAssetName()).Throws<PlatformNotSupportedException>();
    }
}
