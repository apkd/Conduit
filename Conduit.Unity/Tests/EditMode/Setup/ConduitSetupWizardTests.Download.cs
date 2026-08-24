#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    [Test]
    public void PrepareDestinationForOverwriteStopsCurrentPlatformExecutable()
    {
        string destinationPath = Application.platform == RuntimePlatform.WindowsEditor
            ? Path.Combine(projectRoot, "Conduit", "conduit.exe")
            : Path.Combine(projectRoot, "Conduit", "conduit");
        string stoppedPath = string.Empty;
        ServerInstaller.StopRunningExecutableOverride = path => stoppedPath = path;

        ServerInstaller.PrepareDestinationForOverwrite(destinationPath);

        Assert.That(stoppedPath, Is.EqualTo(Path.GetFullPath(destinationPath)));
    }

    [Test]
    public void DownloadedExecutableReplacementSwapsTheCompleteStagedFile()
    {
        string destinationPath = Path.Combine(tempRoot, "conduit-replace-destination");
        string stagedPath = Path.Combine(tempRoot, "conduit-replace-staged");
        using var destinationScope = new FileScope(destinationPath);
        using var stagedScope = new FileScope(stagedPath);
        File.WriteAllText(destinationPath, "old executable");
        File.WriteAllText(stagedPath, "new executable");

        ServerInstaller.ReplaceDownloadedFile(stagedPath, destinationPath);

        Assert.That(File.ReadAllText(destinationPath), Is.EqualTo("new executable"));
        Assert.That(File.Exists(stagedPath), Is.False);
    }

    [Test]
    public void IsNixOsLinuxDetectsNixOsReleaseId()
    {
        var files = new System.Collections.Generic.Dictionary<string, string[]>
        {
            ["/etc/os-release"] = new[]
            {
                "NAME=\"NixOS\"",
                "ID=nixos",
            },
        };

        Assert.That(
            ServerInstallation.IsNixOsLinux(
                isLinux: true,
                files.ContainsKey,
                path => files[path]
            ),
            Is.True
        );
    }

    [Test]
    public void IsNixOsLinuxRejectsRegularDistrosAndNonLinux()
    {
        var files = new System.Collections.Generic.Dictionary<string, string[]>
        {
            ["/etc/os-release"] = new[]
            {
                "NAME=\"Ubuntu\"",
                "ID=ubuntu",
                "ID_LIKE=debian",
            },
        };

        Assert.That(
            ServerInstallation.IsNixOsLinux(
                isLinux: true,
                files.ContainsKey,
                path => files[path]
            ),
            Is.False
        );
        Assert.That(
            ServerInstallation.IsNixOsLinux(
                isLinux: false,
                files.ContainsKey,
                path => files[path]
            ),
            Is.False
        );
        Assert.That(
            ServerInstallation.IsNixOsLinux(
                isLinux: true,
                _ => true,
                _ => throw new IOException("blocked")
            ),
            Is.False
        );
    }

}
