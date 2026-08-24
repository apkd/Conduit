#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    [Test]
    public void EditorSpecsExcludeRemovedAutoconfiguration()
    {
        foreach (var spec in EditorClientCatalog.GetEditorSpecs())
        {
            Assert.That(spec.Id, Is.Not.EqualTo("roo-code"));
            Assert.That(spec.Id, Is.Not.EqualTo("unity-ai"));
        }
    }

    [Test]
    public void JunieAppearsAsOneEditorOption()
    {
        var spec = EditorClientCatalog.FindEditorSpec("rider-junie");

        Assert.That(spec.DisplayName, Is.EqualTo("Junie"));
        Assert.That(spec.ManualSetupSection, Is.EqualTo("JetBrains IDEs / Junie"));
    }

    [TestCase("antigravity")]
    [TestCase("claude-code")]
    [TestCase("codex")]
    [TestCase("continue")]
    [TestCase("cursor")]
    [TestCase("gemini")]
    [TestCase("github-copilot-cli")]
    [TestCase("kilo-code")]
    [TestCase("open-code")]
    [TestCase("rider-junie")]
    [TestCase("vs-copilot")]
    [TestCase("vscode-copilot")]
    [TestCase("zed")]
    public void EditorSpecsWithProjectConfigurationDefaultToProject(string editorId)
    {
        var spec = EditorClientCatalog.FindEditorSpec(editorId);

        Assert.That(EditorConfigurationPaths.SupportsProjectConfiguration(spec), Is.True);
        Assert.That(spec.ResolveProjectConfigPath, Is.Not.Null);
        Assert.That(spec.ResolveUserConfigPath, Is.Not.Null);
        Assert.That(
            EditorConfigurationPaths.GetDefaultConfigurationLocation(spec),
            Is.EqualTo(SetupConfigurationLocation.Project)
        );
    }

    [TestCase("claude-desktop")]
    [TestCase("cline")]
    [TestCase("windsurf")]
    public void EditorSpecsWithoutProjectConfigurationDefaultToUser(string editorId)
    {
        var spec = EditorClientCatalog.FindEditorSpec(editorId);

        Assert.That(EditorConfigurationPaths.SupportsProjectConfiguration(spec), Is.False);
        Assert.That(spec.ResolveProjectConfigPath, Is.Null);
        Assert.That(spec.ResolveUserConfigPath, Is.Not.Null);
        Assert.That(
            EditorConfigurationPaths.GetDefaultConfigurationLocation(spec),
            Is.EqualTo(SetupConfigurationLocation.User)
        );
    }

    [Test]
    public void ConfigureEditorWritesOnlyTheSelectedConfigurationLocation()
    {
        var spec = CreateTempSpec("cursor", "cursor-user-scope.json");
        string projectConfigPath = Path.Combine(tempRoot, "cursor-project-scope.json");
        string userConfigPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-cursor-scope.exe");
        spec.ResolveProjectConfigPath = _ => projectConfigPath;
        using var projectScope = new FileScope(projectConfigPath);
        using var userScope = new FileScope(userConfigPath);
        DeleteConfig(projectConfigPath);
        DeleteConfig(userConfigPath);

        EditorConfiguration.ConfigureEditor(
            spec,
            SetupConfigurationLocation.Project,
            executablePath
        );

        Assert.That(File.Exists(projectConfigPath), Is.True);
        Assert.That(File.Exists(userConfigPath), Is.False);
        Assert.That(
            EditorConfigurationPaths.GetDisplayConfigPath(
                spec,
                SetupConfigurationLocation.Project
            ),
            Is.EqualTo(projectConfigPath)
        );
        Assert.That(
            EditorConfigurationPaths.GetDisplayConfigPath(
                spec,
                SetupConfigurationLocation.User
            ),
            Is.EqualTo(userConfigPath)
        );
    }

    [Test]
    public void ExistingUserProfileConfigurationIsPreferredOverTheProjectDefault()
    {
        var spec = CreateTempSpec("cursor", "cursor-user-preferred.json");
        string userConfigPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string projectConfigPath = Path.Combine(tempRoot, "cursor-project-preferred.json");
        string executablePath = CreateExecutable("conduit-cursor-user-preferred.exe");
        spec.ResolveProjectConfigPath = _ => projectConfigPath;
        using var projectScope = new FileScope(projectConfigPath);
        using var userScope = new FileScope(userConfigPath);
        DeleteConfig(projectConfigPath);
        DeleteConfig(userConfigPath);
        EditorConfiguration.ConfigureEditor(
            spec,
            SetupConfigurationLocation.User,
            executablePath
        );

        var location = EditorConfigurationPaths.GetPreferredConfigurationLocation(
            spec,
            SetupConfigurationLocation.Project
        );

        Assert.That(
            location,
            Is.EqualTo(SetupConfigurationLocation.User)
        );
    }

    [Test]
    public void VisualStudioAutoconfigurationIsWindowsOnly()
    {
        string? configPath = EditorConfigurationPaths.GetConfigPath(
            EditorClientCatalog.FindEditorSpec("vs-copilot")
        );

        Assert.That(
            configPath,
            Application.platform == RuntimePlatform.WindowsEditor ? Is.Not.Null : Is.Null
        );
    }

    [Test]
    public void UserConfigurationDetectionChecksAlternateConfigPaths()
    {
        var spec = CreateTempSpec("cline", "cline-detection-primary.json");
        string primaryPath = Path.Combine(tempRoot, "cline-detection-primary.json");
        string alternatePath = Path.Combine(tempRoot, "cline-detection-alternate.json");
        using var primaryScope = new FileScope(primaryPath);
        using var alternateScope = new FileScope(alternatePath);
        DeleteConfig(primaryPath);
        DeleteConfig(alternatePath);
        spec.ResolveUserConfigPaths = _ => new[] { alternatePath };

        Assert.That(EditorConfigurationPaths.HasUserConfigurationFile(spec), Is.False);

        File.WriteAllText(alternatePath, "{}");

        Assert.That(EditorConfigurationPaths.HasUserConfigurationFile(spec), Is.True);
    }

    [Test]
    public void UpdatedServerPathMakesConfigureButtonActionable()
    {
        var spec = CreateTempSpec("cursor", "cursor-updated.json");
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string configuredExecutablePath = CreateExecutable("conduit-configured.exe");
        string updatedExecutablePath = CreateExecutable("conduit-updated.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"existing\": {\n" +
            "      \"command\": \"" + configuredExecutablePath.Replace("\\", "\\\\") + "\",\n" +
            "      \"args\": []\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        var button = SetupActionEvaluator.EvaluateConfigureButton(spec, updatedExecutablePath, false, false);

        Assert.That(button.State, Is.EqualTo(SetupActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Configure Cursor"));
    }

}
