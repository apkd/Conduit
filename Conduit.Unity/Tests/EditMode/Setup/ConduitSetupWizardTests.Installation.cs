#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    [Test]
    public void OlderExecutableVersionTurnsDownloadIntoUpdate()
    {
        string executablePath = CreateExecutable(
            Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit"
        );
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () => "0.2.8";
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => "0.2.7+sha.old";

        var button = SetupActionEvaluator.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(SetupActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Update the MCP server"));
        Assert.That(
            button.Hint,
            Does.Contain(
                "installed server version 0.2.7+sha.old is older than " +
                "the Unity package version 0.2.8"
            )
        );
    }

    [Test]
    public void MatchingExecutableVersionKeepsDownloadedState()
    {
        string executablePath = CreateExecutable(
            Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit"
        );
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () => "0.2.8";
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => "0.2.8+sha.current";

        var button = SetupActionEvaluator.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(SetupActionState.Success));
        Assert.That(button.Label, Is.EqualTo("MCP server installed"));
        Assert.That(button.Hint, Does.Contain(executablePath));
    }

    [Test]
    public void BrokenExecutableVersionTurnsDownloadIntoReinstall()
    {
        string executablePath = CreateExecutable(
            Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit"
        );
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => null;

        var button = SetupActionEvaluator.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(SetupActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Reinstall the MCP server"));
        Assert.That(button.Hint, Does.Contain("could not report its version"));
    }

    [Test]
    public void GloballyDiscoveredExecutableCountsAsInstalled()
    {
        string executablePath = CreateExecutable("conduit-global");
        ServerExecutableLocator.DiscoverExecutableOverride = () => executablePath;

        var button = SetupActionEvaluator.EvaluateDownloadButton(string.Empty, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(SetupActionState.Success));
        Assert.That(button.Label, Is.EqualTo("MCP server installed"));
        Assert.That(button.Hint, Does.Contain(executablePath));
    }

    [Test]
    public void UserProfileDownloadTargetsOnlyTheCurrentOperatingSystem()
    {
        var targets = ServerInstallation.CreateDownloadTargets(
            SetupConfigurationLocation.User,
            string.Empty
        );
        string expectedPath = ServerInstallation.GetUserInstalledExecutablePath(
            Application.platform,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        );

        Assert.That(targets, Has.Length.EqualTo(1));
        Assert.That(targets[0].DestinationPath, Is.EqualTo(expectedPath));
        Assert.That(
            targets[0].Url,
            Application.platform == RuntimePlatform.WindowsEditor
                ? Does.EndWith("/conduit-win-x64.exe")
                : Does.EndWith("/" + ServerInstallation.GetLinuxDownloadAssetName())
        );
    }

    [Test]
    public void UserProfileExecutableUsesThePlatformUserDirectory()
    {
        string userHome = Path.Combine(tempRoot, "home");
        string localAppData = Path.Combine(tempRoot, "local-app-data");

        Assert.That(
            ServerInstallation.GetUserInstalledExecutablePath(
                RuntimePlatform.WindowsEditor,
                userHome,
                localAppData
            ),
            Is.EqualTo(Path.Combine(localAppData, "Conduit", "conduit.exe"))
        );
        Assert.That(
            ServerInstallation.GetUserInstalledExecutablePath(
                RuntimePlatform.LinuxEditor,
                userHome,
                localAppData
            ),
            Is.EqualTo(Path.Combine(userHome, ".local", "bin", "conduit"))
        );
    }

    [Test]
    public void UserProfileUsesItsConfiguredExecutableBeforeTheHomeInstallPath()
    {
        string configuredExecutablePath = CreateExecutable("conduit-user-profile-outdated");
        string userExecutablePath = Path.Combine(tempRoot, "user-bin", "conduit");
        ServerInstallation.GetUserInstalledExecutablePathOverride = () => userExecutablePath;
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () => "0.3.13";
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => "0.3.12";

        var button = SetupActionEvaluator.EvaluateDownloadButton(
            SetupConfigurationLocation.User,
            string.Empty,
            configuredExecutablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Update the MCP server"));
        Assert.That(button.Hint, Does.Contain(configuredExecutablePath));
        Assert.That(button.Hint, Does.Not.Contain(userExecutablePath));
        Assert.That(button.IsOutdated, Is.True);
    }

    [Test]
    public void ConfigurationLocationsDoNotBorrowPersistedExecutablesFromEachOther()
    {
        string externalExecutablePath = Path.Combine(
            Path.GetTempPath(),
            $"conduit-user-profile-{Guid.NewGuid():N}"
        );
        string projectExecutablePath = Application.platform == RuntimePlatform.WindowsEditor
            ? Path.Combine(projectRoot, "Conduit", "conduit.exe")
            : Path.Combine(projectRoot, "Conduit", "conduit");
        using var externalScope = new FileScope(externalExecutablePath);
        using var projectScope = new FileScope(projectExecutablePath);
        File.WriteAllText(externalExecutablePath, "echo conduit");
        if (File.Exists(projectExecutablePath))
            File.Delete(projectExecutablePath);

        string projectResult = ServerExecutableLocator.GetEffectiveExecutablePath(
            SetupConfigurationLocation.Project,
            externalExecutablePath,
            string.Empty
        );
        string userResult = ServerExecutableLocator.GetEffectiveExecutablePath(
            SetupConfigurationLocation.User,
            string.Empty,
            externalExecutablePath
        );

        Assert.That(projectResult, Is.Empty);
        Assert.That(userResult, Is.EqualTo(externalExecutablePath));
    }

    [Test]
    public void PathDiscoveryFindsConduitWithoutLaunchingPlatformUtilities()
    {
        string executablePath = CreateExecutable("conduit");
        string pathValue =
            $"\"{Path.GetDirectoryName(executablePath)}\"{Path.PathSeparator}{tempRoot}-missing";

        string? discoveredPath = ServerExecutableLocator.FindOnPathValue(
            pathValue,
            "conduit",
            "conduit.exe"
        );

        Assert.That(discoveredPath, Is.EqualTo(Path.GetFullPath(executablePath)));
    }

    [Test]
    public void ConfigPathResolversHonorDocumentedEnvironmentOverrides()
    {
        string clinePath = Path.Combine(tempRoot, "custom-cline.json");
        string codexHome = Path.Combine(tempRoot, "codex-home");
        string geminiHome = Path.Combine(tempRoot, "gemini-home");
        string copilotHome = Path.Combine(tempRoot, "copilot-home");
        string? previousCline = Environment.GetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH");
        string? previousCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        string? previousGemini = Environment.GetEnvironmentVariable("GEMINI_CLI_HOME");
        string? previousCopilot = Environment.GetEnvironmentVariable("COPILOT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH", clinePath);
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", geminiHome);
            Environment.SetEnvironmentVariable("COPILOT_HOME", copilotHome);
            var context = new SetupPathContext(
                Path.Combine(tempRoot, "project"),
                Path.Combine(tempRoot, "home"),
                Path.Combine(tempRoot, "app-data")
            );

            Assert.That(
                EditorClientCatalog.FindEditorSpec("cline").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.GetFullPath(clinePath))
            );
            Assert.That(
                EditorClientCatalog.FindEditorSpec("codex").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.Combine(codexHome, "config.toml"))
            );
            Assert.That(
                EditorClientCatalog.FindEditorSpec("gemini").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.Combine(geminiHome, ".gemini", "settings.json"))
            );
            Assert.That(
                EditorClientCatalog.FindEditorSpec("github-copilot-cli").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.Combine(copilotHome, "mcp-config.json"))
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLINE_MCP_SETTINGS_PATH", previousCline);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodex);
            Environment.SetEnvironmentVariable("GEMINI_CLI_HOME", previousGemini);
            Environment.SetEnvironmentVariable("COPILOT_HOME", previousCopilot);
        }
    }

    [Test]
    public void ConfiguredBareCommandResolvesThroughPath()
    {
        string executablePath = CreateExecutable(
            Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit"
        );
        string configPath = Path.Combine(tempRoot, "codex-bare-command.toml");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        File.WriteAllText(
            configPath,
            "[mcp_servers.unity]\nenabled = true\ncommand = \"conduit\"\n"
        );

        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(executablePath));
            var spec = EditorClientCatalog.FindEditorSpec("codex");

            Assert.That(
                EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath),
                Is.True
            );
            Assert.That(
                EditorConfiguration.TryGetConfiguredExecutablePath(
                    spec,
                    configPath,
                    out var configuredPath
                ),
                Is.True
            );
            Assert.That(SetupPathUtility.PathsEqual(configuredPath, executablePath), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Test]
    public void OutdatedExecutableOutsideHomeAndProjectCannotBeOverwritten()
    {
        string executablePath = Path.Combine(
            Path.GetTempPath(),
            $"conduit-outside-{Guid.NewGuid():N}"
        );
        using var scope = new FileScope(executablePath);
        File.WriteAllText(executablePath, "echo conduit");
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => "0.3.11";

        var button = SetupActionEvaluator.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(SetupActionState.Error));
        Assert.That(button.Label, Is.EqualTo("MCP server binary is outdated but not writeable"));
        Assert.That(
            button.Hint,
            Is.EqualTo(
                $"Conduit cannot automatically update MCP server executables in this path: `{executablePath}`."
            )
        );
        Assert.That(button.IsOutdated, Is.True);
    }

    [TestCase("/run/current-system/sw/bin/conduit")]
    [TestCase("/nix/store/hash-conduit/bin/conduit")]
    public void NixOsManagedExecutableDefersWithoutVersionProbe(string executablePath)
    {
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () =>
            throw new InvalidOperationException("NixOS-managed executables must not be version-probed.");
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ =>
            throw new InvalidOperationException("NixOS-managed executables must not be version-probed.");

        var button = SetupActionEvaluator.EvaluateDownloadButtonCore(
            SetupConfigurationLocation.User,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Disabled));
        Assert.That(
            button.Hint,
            Does.Contain("Update the package through your NixOS configuration")
        );
    }

    [TestCase("/run/current-system-old/sw/bin/conduit")]
    [TestCase("/nix/storehouse/hash-conduit/bin/conduit")]
    public void NixOsManagedExecutablePathRejectsLookalikes(string executablePath)
        => Assert.That(
            ServerInstallation.IsNixOsManagedExecutablePath(executablePath),
            Is.False
        );

    [Test]
    public void OutdatedReadOnlyExecutableCannotBeOverwritten()
    {
        string executablePath = CreateExecutable("conduit-read-only");
        ServerVersionProbe.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ServerVersionProbe.ProbeExecutableVersionOverride = static _ => "0.3.11";
        File.SetAttributes(executablePath, File.GetAttributes(executablePath) | FileAttributes.ReadOnly);

        try
        {
            var button = SetupActionEvaluator.EvaluateDownloadButton(executablePath, string.Empty, false, false);

            Assert.That(button.State, Is.EqualTo(SetupActionState.Error));
            Assert.That(button.Label, Is.EqualTo("MCP server binary is outdated but not writeable"));
            Assert.That(button.Hint, Does.Contain("cannot replace"));
        }
        finally
        {
            File.SetAttributes(executablePath, FileAttributes.Normal);
        }
    }

    [Test]
    public void AutomaticUpdateLocationAcceptsHomeAndProjectDescendants()
    {
        string homePath = Path.Combine(tempRoot, "home");
        string otherProjectPath = Path.Combine(tempRoot, "project");

        Assert.That(
            ServerInstallation.IsAutomaticUpdateLocation(
                Path.Combine(homePath, ".local", "bin", "conduit"),
                homePath,
                otherProjectPath
            ),
            Is.True
        );
        Assert.That(
            ServerInstallation.IsAutomaticUpdateLocation(
                Path.Combine(otherProjectPath, "Conduit", "conduit"),
                homePath,
                otherProjectPath
            ),
            Is.True
        );
        Assert.That(
            ServerInstallation.IsAutomaticUpdateLocation(
                Path.Combine(tempRoot, "system", "conduit"),
                homePath,
                otherProjectPath
            ),
            Is.False
        );
    }

    [Test]
    public void PackageUpdateComparisonUsesInstalledAndReleaseHashes()
    {
        var current = ConduitPackageUpdater.CompareHashes("abc", "ABC");
        var outdated = ConduitPackageUpdater.CompareHashes("abc", "def");

        Assert.That(current.State, Is.EqualTo(ConduitPackageUpdateState.Current));
        Assert.That(outdated.State, Is.EqualTo(ConduitPackageUpdateState.Outdated));
        Assert.That(outdated.InstalledHash, Is.EqualTo("abc"));
        Assert.That(outdated.LatestHash, Is.EqualTo("def"));
    }

    [Test]
    public void PackageUpdateCheckOnlySupportsOfficialGitInstall()
    {
        Assert.That(
            ConduitPackageUpdater.IsOfficialGitPackage(
                UnityEditor.PackageManager.PackageSource.Git,
                "dev.tryfinally.conduit@https://github.com/apkd/Conduit.git?path=/Conduit.Unity#release"
            ),
            Is.True
        );
        Assert.That(
            ConduitPackageUpdater.IsOfficialGitPackage(
                UnityEditor.PackageManager.PackageSource.Local,
                "dev.tryfinally.conduit@file:../Conduit.Unity"
            ),
            Is.False
        );
        Assert.That(
            ConduitPackageUpdater.IsOfficialGitPackage(
                UnityEditor.PackageManager.PackageSource.Git,
                "dev.tryfinally.conduit@https://github.com/example/Conduit.git"
            ),
            Is.False
        );
    }

}
