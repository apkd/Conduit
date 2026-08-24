#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    static readonly string[] jsonEditorIds =
    {
        "antigravity",
        "claude-code",
        "claude-desktop",
        "cline",
        "continue",
        "cursor",
        "gemini",
        "github-copilot-cli",
        "kilo-code",
        "open-code",
        "rider-junie",
        "vs-copilot",
        "vscode-copilot",
        "windsurf",
        "zed",
    };

    string projectRoot = null!;
    string tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
        tempRoot = Path.Combine(projectRoot, "Temp", "ConduitSetupWizardTests");
        Directory.CreateDirectory(tempRoot);
        ServerInstaller.ResetInstallStateForTests();
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => "0.3.12";
        ServerExecutableLocator.DiscoverExecutableOverride = static () => null;
    }

    [TearDown]
    public void TearDown()
        => ServerInstaller.ResetInstallStateForTests();

}
