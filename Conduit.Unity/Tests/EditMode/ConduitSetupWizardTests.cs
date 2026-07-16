#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed class ConduitSetupWizardTests
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
        ConduitSetupWizardUtility.ResetInstallStateForTests();
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.3.12";
        ConduitSetupWizardUtility.DiscoverExecutableOverride = static () => null;
    }

    [TearDown]
    public void TearDown()
        => ConduitSetupWizardUtility.ResetInstallStateForTests();

    [Test]
    public void ConfigureEditor_KiloCodeCreatesConfigAndDetectsIt()
    {
        var spec = CreateTempSpec("kilo-code", "kilo.json");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-kilo.exe");
        using var scope = new FileScope(configPath!);
        DeleteConfig(configPath!);

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(File.Exists(configPath!), Is.True);
        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        string config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("\"mcp\""));
        Assert.That(config, Does.Contain("\"enabled\": true"));
        Assert.That(config, Does.Contain("\"type\": \"local\""));
        Assert.That(config, Does.Contain("\"command\": ["));
        Assert.That(config, Does.Not.Contain("\"args\""));
    }

    [Test]
    public void ConfigureEditor_OpenCodeWritesCommandArray()
    {
        var spec = CreateTempSpec("open-code", "opencode.json");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-open-code.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(configPath!, "{\n  \"mcp\": {}\n}\n");

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        string config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("\"type\": \"local\""));
        Assert.That(config, Does.Contain("\"command\": ["));
        Assert.That(config, Does.Contain(Path.GetFileName(executablePath)));
    }

    [Test]
    public void ConfigureButton_PrefersTheEditorsEffectiveExistingConfig()
    {
        var spec = CreateTempSpec("open-code", "opencode-create-target.json");
        string createTarget = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string existingTarget = Path.Combine(tempRoot, "opencode-existing.jsonc");
        string executablePath = CreateExecutable("conduit-opencode-existing.exe");
        spec.ResolveUserConfigPaths = _ => new[] { existingTarget };
        File.WriteAllText(existingTarget, "{\"mcp\":{}}\n");
        if (File.Exists(createTarget))
            File.Delete(createTarget);

        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("JSONC"));
        Assert.That(File.Exists(createTarget), Is.False);
    }

    [Test]
    public void ConfigureButton_RejectsAmbiguousWindsurfConfigPaths()
    {
        var spec = CreateTempSpec("windsurf", "windsurf-primary.json");
        string primaryPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string alternatePath = Path.Combine(tempRoot, "windsurf-alternate.json");
        string executablePath = CreateExecutable("conduit-windsurf-ambiguous.exe");
        spec.ResolveUserConfigPaths = _ => new[] { primaryPath, alternatePath };
        File.WriteAllText(primaryPath, "{}");
        File.WriteAllText(alternatePath, "{}");

        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("More than one Windsurf MCP config file"));
        Assert.Throws<InvalidOperationException>(
            () => ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath)
        );
    }

    [Test]
    public void ConfigureEditor_ClineWritesCurrentFlatSchema()
    {
        var spec = CreateTempSpec("cline", "cline_mcp_settings.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-cline.exe");
        File.WriteAllText(
            configPath,
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"unity\": {\n" +
            "      \"command\": \"" + executablePath.Replace("\\", "\\\\") + "\",\n" +
            "      \"args\": [\"--old\"],\n" +
            "      \"cwd\": \"/work\",\n" +
            "      \"env\": {\"KEEP\": \"yes\"},\n" +
            "      \"url\": \"http://127.0.0.1:5080\",\n" +
            "      \"alwaysAllow\": [\"status\"]\n" +
            "    }\n" +
            "  }\n" +
            "}\n"
        );

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(
            ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(spec, configPath, out var configuredPath),
            Is.True
        );
        Assert.That(configuredPath, Is.EqualTo(executablePath));

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        string config = File.ReadAllText(configPath);
        Assert.That(config, Does.Not.Contain("\"transport\""));
        Assert.That(config, Does.Contain("\"type\": \"stdio\""));
        Assert.That(config, Does.Contain("\"disabled\": false"));
        Assert.That(config, Does.Contain("\"alwaysAllow\""));
        Assert.That(config, Does.Contain("\"cwd\": \"/work\""));
        Assert.That(config, Does.Contain("\"KEEP\": \"yes\""));
        Assert.That(config, Does.Not.Contain("127.0.0.1"));
        Assert.That(config.Split("\"command\"").Length - 1, Is.EqualTo(1));
        Assert.That(config, Does.Not.Contain("127.0.0.1"));
    }

    [TestCase("continue", "continue.json", "\"mcpServers\"", "\"type\": \"stdio\"")]
    [TestCase("windsurf", "windsurf.json", "\"mcpServers\"", "\"args\": []")]
    [TestCase("zed", "zed.json", "\"context_servers\"", "\"args\": []")]
    public void ConfigureEditor_NewWizardTargetsCreateValidConfig(
        string editorId,
        string configFileName,
        string expectedBody,
        string expectedSetting
    )
    {
        var spec = CreateTempSpec(editorId, configFileName);
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable($"conduit-{editorId}.exe");
        DeleteConfig(configPath);

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        string config = File.ReadAllText(configPath);
        Assert.That(config, Does.Contain(expectedBody));
        Assert.That(config, Does.Contain(expectedSetting));
    }

    [TestCaseSource(nameof(jsonEditorIds))]
    public void ConfigureEditor_EveryJsonTargetRoundTrips(string editorId)
    {
        var spec = CreateTempSpec(editorId, $"{editorId}-roundtrip.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable($"conduit-{editorId}-roundtrip.exe");
        if (spec.CreateOnlyConfig)
            DeleteConfig(configPath);
        else
            File.WriteAllText(configPath, "{\"unrelated\":{\"value\":7}}\n");

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        if (!spec.CreateOnlyConfig)
            Assert.That(File.ReadAllText(configPath), Does.Contain("\"unrelated\""));
    }

    [Test]
    public void ConfigureEditor_CopilotCliIncludesRequiredToolWildcard()
    {
        var spec = CreateTempSpec("github-copilot-cli", "copilot-mcp.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-copilot.exe");
        DeleteConfig(configPath);

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(File.ReadAllText(configPath), Does.Contain("\"tools\": [\n        \"*\""));
    }

    [Test]
    public void ConfigureEditor_CodexCreatesMissingConfig()
    {
        var spec = CreateTempSpec("codex", "codex-missing.toml");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-codex-missing.exe");
        DeleteConfig(configPath);

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(File.ReadAllText(configPath), Does.Contain("[mcp_servers.unity]"));
    }

    [Test]
    public void ConfigureCodexPermissions_PreservesFileAndDetectsApprovals()
    {
        var spec = CreateTempSpec("codex", "codex-config.toml");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-codex.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "[mcp_servers.other]\n" +
            "command = \"elsewhere\"\n\n" +
            "[mcp_servers.unity]\n" +
            "startup_timeout_sec = 15\n" +
            "url = \"http://127.0.0.1:5080\"\n" +
            "bearer_token = \"stale\"\n"
        );

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);
        ConduitSetupWizardUtility.ConfigureCodexPermissions(configPath!);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(ConduitSetupWizardUtility.HasCodexPermissions(configPath!), Is.True);

        string config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("[mcp_servers.other]"));
        Assert.That(config, Does.Contain("tools.playmode.approval_mode = \"approve\""));
        Assert.That(config, Does.Contain("tools.editmode.approval_mode = \"approve\""));
        Assert.That(config, Does.Contain("tools.profiler_record.approval_mode = \"approve\""));
        Assert.That(config, Does.Contain("tools.view_burst_asm.approval_mode = \"approve\""));
        Assert.That(config, Does.Contain("startup_timeout_sec = 15"));
        Assert.That(config, Does.Contain("args = []"));
        Assert.That(config, Does.Contain("tool_timeout_sec = 300"));
        Assert.That(config, Does.Not.Contain("bearer_token"));
        Assert.That(config, Does.Not.Contain("url ="));
    }

    [Test]
    public void CurrentReadersAcceptOptionalTypeAndEnabledFields()
    {
        string executablePath = CreateExecutable("conduit-optional-fields.exe");
        string cursorPath = Path.Combine(tempRoot, "cursor-optional.json");
        string kiloPath = Path.Combine(tempRoot, "kilo-optional.json");
        string codexPath = Path.Combine(tempRoot, "codex-optional.toml");
        File.WriteAllText(
            cursorPath,
            "{\"mcpServers\":{\"unity\":{\"command\":\"" + JsonPath(executablePath) + "\"}}}"
        );
        File.WriteAllText(
            kiloPath,
            "{\"mcp\":{\"unity\":{\"type\":\"local\",\"command\":[\"" + JsonPath(executablePath) + "\"]}}}"
        );
        File.WriteAllText(
            codexPath,
            "[mcp_servers.unity]\ncommand = \"" + executablePath.Replace("\\", "\\\\") + "\"\n"
        );

        Assert.That(
            ConduitSetupWizardUtility.IsEditorConfigured(
                ConduitSetupWizardUtility.FindEditorSpec("cursor"),
                cursorPath,
                executablePath
            ),
            Is.True
        );
        Assert.That(
            ConduitSetupWizardUtility.IsEditorConfigured(
                ConduitSetupWizardUtility.FindEditorSpec("kilo-code"),
                kiloPath,
                executablePath
            ),
            Is.True
        );
        Assert.That(
            ConduitSetupWizardUtility.IsEditorConfigured(
                ConduitSetupWizardUtility.FindEditorSpec("codex"),
                codexPath,
                executablePath
            ),
            Is.True
        );

        static string JsonPath(string path) => path.Replace("\\", "\\\\");
    }

    [Test]
    public void DisabledServerMakesConfigureButtonActionable()
    {
        var spec = CreateTempSpec("antigravity", "antigravity-disabled.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-antigravity-disabled.exe");
        File.WriteAllText(
            configPath,
            "{\"mcpServers\":{\"unity\":{\"command\":\"" +
            executablePath.Replace("\\", "\\\\") +
            "\",\"args\":[],\"disabled\":true}}}"
        );

        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
    }

    [Test]
    public void ExistingZedSettingsAreLeftForLosslessManualEditing()
    {
        var spec = CreateTempSpec("zed", "zed-existing.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-zed-existing.exe");
        const string existingSettings = "{\n  // keep this comment\n  \"theme\": \"One Dark\"\n}\n";
        File.WriteAllText(configPath, existingSettings);

        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("will not rewrite"));
        Assert.Throws<InvalidOperationException>(
            () => ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath)
        );
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(existingSettings));
    }

    [Test]
    public void ExistingJsoncConfigCanBeDetectedWithoutRewritingIt()
    {
        var spec = CreateTempSpec("kilo-code", "kilo-existing.jsonc");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-kilo-jsonc.exe");
        string jsonPath = executablePath.Replace("\\", "\\\\");
        string existingConfig =
            "{\n" +
            "  // Kilo accepts JSONC.\n" +
            "  \"mcp\": {\n" +
            "    \"unity\": {\n" +
            "      \"type\": \"local\",\n" +
            "      \"command\": [\"" + jsonPath + "\"],\n" +
            "    },\n" +
            "  },\n" +
            "}\n";
        File.WriteAllText(configPath, existingConfig);

        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(existingConfig));
    }

    [Test]
    public void ExistingCommentedJsonIsLeftForLosslessManualEditing()
    {
        var spec = CreateTempSpec("vscode-copilot", "vscode-commented.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-vscode-commented.exe");
        const string existingConfig =
            "{\n" +
            "  \"documentation\": \"http://example.com\",\n" +
            "  // keep this explanation\n" +
            "  \"servers\": {}\n" +
            "}\n";
        File.WriteAllText(configPath, existingConfig);

        Assert.That(
            ConduitSimpleJson.ContainsComments("{\"url\":\"http://example.com\"}"),
            Is.False
        );
        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("contains comments"));
        Assert.Throws<InvalidOperationException>(
            () => ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath)
        );
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(existingConfig));
    }

    [Test]
    public void ConfigureEditor_DoesNotOverwriteMalformedConfigStructure()
    {
        var spec = CreateTempSpec("cursor", "cursor-malformed.json");
        string configPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-cursor-malformed.exe");
        const string malformedConfig = "{\"theme\":\"dark\",\"mcpServers\":[]}";
        File.WriteAllText(configPath, malformedConfig);

        Assert.Throws<InvalidOperationException>(
            () => ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath)
        );
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(malformedConfig));
    }

    [Test]
    public void ExistingConfigWithConduitCommandCountsAsDownloadedAndConfigured()
    {
        var spec = CreateTempSpec("cursor", "cursor-existing.json");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"existing\": {\n" +
            "      \"command\": \"" + executablePath.Replace("\\", "\\\\") + "\",\n" +
            "      \"args\": []\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        Assert.That(
            ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(
                spec,
                configPath,
                out var configuredPath
            ),
            Is.True
        );
        Assert.That(configuredPath, Is.EqualTo(executablePath));

        var downloadButton = ConduitSetupWizardUtility.EvaluateDownloadButton(
            string.Empty,
            configuredPath,
            false,
            false
        );
        var configureButton = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(configureButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
    }

    [Test]
    public void DiscoveryScansEveryKnownEditorConfigForAnExistingExecutable()
    {
        var spec = CreateTempSpec("cursor", "cursor-discovery.json");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-discovered");
        string projectConfigPath = Path.Combine(tempRoot, "cursor-project-discovery.json");
        spec.ResolveProjectConfigPath = _ => projectConfigPath;
        using var scope = new FileScope(configPath!);
        using var projectScope = new FileScope(projectConfigPath);
        DeleteConfig(configPath!);
        DeleteConfig(projectConfigPath);
        ConduitSetupWizardUtility.ConfigureEditor(
            spec,
            ConduitSetupWizardUtility.ConfigurationLocation.User,
            executablePath
        );

        bool found = ConduitSetupWizardUtility.TryGetAnyConfiguredExecutablePath(
            new[] { spec },
            out var discoveredExecutablePath,
            out var discoveredConfigPath
        );

        Assert.That(found, Is.True);
        Assert.That(discoveredExecutablePath, Is.EqualTo(executablePath));
        Assert.That(discoveredConfigPath, Is.EqualTo(configPath));
        Assert.That(
            ConduitSetupWizardUtility.TryGetAnyConfiguredExecutablePath(
                new[] { spec },
                ConduitSetupWizardUtility.ConfigurationLocation.User,
                out _,
                out _
            ),
            Is.True
        );
        Assert.That(
            ConduitSetupWizardUtility.TryGetAnyConfiguredExecutablePath(
                new[] { spec },
                ConduitSetupWizardUtility.ConfigurationLocation.Project,
                out _,
                out _
            ),
            Is.False
        );
    }

    [Test]
    public void MissingConfiguredConduitExecutableDoesNotCountAsDownloaded()
    {
        var spec = CreateTempSpec("cursor", "cursor-missing.json");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string installedExecutablePath = Application.platform == RuntimePlatform.WindowsEditor
            ? Path.Combine(projectRoot, "Conduit", "conduit.exe")
            : Path.Combine(projectRoot, "Conduit", "conduit");
        string missingExecutablePath = Path.Combine(tempRoot, "missing-conduit.exe");
        using var installedScope = new FileScope(installedExecutablePath);
        using var scope = new FileScope(configPath!);
        if (File.Exists(installedExecutablePath))
            File.Delete(installedExecutablePath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "{\n" +
            "  \"mcpServers\": {\n" +
            "    \"existing\": {\n" +
            "      \"command\": \"" + missingExecutablePath.Replace("\\", "\\\\") + "\",\n" +
            "      \"args\": []\n" +
            "    }\n" +
            "  }\n" +
            "}\n");

        Assert.That(ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(spec, configPath, out _), Is.False);

        var downloadButton = ConduitSetupWizardUtility.EvaluateDownloadButton(
            string.Empty,
            missingExecutablePath,
            false,
            false
        );
        var configureButton = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
        Assert.That(configureButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
    }

    [Test]
    public void ExistingConfigWithWindowsWslPathCountsAsDownloadedAndConfigured()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
            Assert.Ignore("This scenario is only relevant on Windows.");

        var spec = CreateTempSpec("codex", "codex-wsl.toml");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string windowsExecutablePath = CreateExecutable("conduit-wsl.exe");
        string driveRoot = Path.GetPathRoot(windowsExecutablePath)!;
        char driveLetter = char.ToLowerInvariant(driveRoot[0]);
        string wslExecutablePath =
            "/mnt/" + driveLetter + "/" + windowsExecutablePath[driveRoot.Length..].Replace('\\', '/');

        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "[mcp_servers.unity]\n" +
            "command = \"" + wslExecutablePath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\n"
        );

        Assert.That(
            ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(
                spec,
                configPath,
                out var configuredPath
            ),
            Is.True
        );
        Assert.That(Path.GetFullPath(configuredPath), Is.EqualTo(Path.GetFullPath(windowsExecutablePath)));

        var downloadButton = ConduitSetupWizardUtility.EvaluateDownloadButton(
            string.Empty,
            configuredPath,
            false,
            false
        );
        var configureButton = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(configureButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
    }

    [Test]
    public void OlderExecutableVersionTurnsDownloadIntoUpdate()
    {
        string executablePath = CreateExecutable(
            Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit"
        );
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.2.8";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.2.7+sha.old";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
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
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.2.8";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.2.8+sha.current";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(button.Label, Is.EqualTo("MCP server installed"));
        Assert.That(button.Hint, Does.Contain(executablePath));
    }

    [Test]
    public void BrokenExecutableVersionTurnsDownloadIntoReinstall()
    {
        string executablePath = CreateExecutable(
            Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit"
        );
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => null;

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Reinstall the MCP server"));
        Assert.That(button.Hint, Does.Contain("could not report its version"));
    }

    [Test]
    public void GloballyDiscoveredExecutableCountsAsInstalled()
    {
        string executablePath = CreateExecutable("conduit-global");
        ConduitSetupWizardUtility.DiscoverExecutableOverride = () => executablePath;

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(string.Empty, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(button.Label, Is.EqualTo("MCP server installed"));
        Assert.That(button.Hint, Does.Contain(executablePath));
    }

    [Test]
    public void UserProfileDownloadTargetsOnlyTheCurrentOperatingSystem()
    {
        var targets = ConduitSetupWizardUtility.CreateDownloadTargets(
            ConduitSetupWizardUtility.ConfigurationLocation.User,
            string.Empty
        );
        string expectedPath = ConduitSetupWizardUtility.GetUserInstalledExecutablePath(
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
                : Does.EndWith("/" + ConduitSetupWizardUtility.GetLinuxDownloadAssetName())
        );
    }

    [Test]
    public void UserProfileExecutableUsesThePlatformUserDirectory()
    {
        string userHome = Path.Combine(tempRoot, "home");
        string localAppData = Path.Combine(tempRoot, "local-app-data");

        Assert.That(
            ConduitSetupWizardUtility.GetUserInstalledExecutablePath(
                RuntimePlatform.WindowsEditor,
                userHome,
                localAppData
            ),
            Is.EqualTo(Path.Combine(localAppData, "Conduit", "conduit.exe"))
        );
        Assert.That(
            ConduitSetupWizardUtility.GetUserInstalledExecutablePath(
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
        ConduitSetupWizardUtility.GetUserInstalledExecutablePathOverride = () => userExecutablePath;
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.3.13";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.3.12";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(
            ConduitSetupWizardUtility.ConfigurationLocation.User,
            string.Empty,
            configuredExecutablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
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

        string projectResult = ConduitSetupWizardUtility.GetEffectiveExecutablePath(
            ConduitSetupWizardUtility.ConfigurationLocation.Project,
            externalExecutablePath,
            string.Empty
        );
        string userResult = ConduitSetupWizardUtility.GetEffectiveExecutablePath(
            ConduitSetupWizardUtility.ConfigurationLocation.User,
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

        string? discoveredPath = ConduitSetupWizardUtility.FindOnPathValue(
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
            var context = new ConduitSetupWizardUtility.PathContext
            {
                ProjectRoot = Path.Combine(tempRoot, "project"),
                UserHome = Path.Combine(tempRoot, "home"),
                AppData = Path.Combine(tempRoot, "app-data"),
            };

            Assert.That(
                ConduitSetupWizardUtility.FindEditorSpec("cline").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.GetFullPath(clinePath))
            );
            Assert.That(
                ConduitSetupWizardUtility.FindEditorSpec("codex").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.Combine(codexHome, "config.toml"))
            );
            Assert.That(
                ConduitSetupWizardUtility.FindEditorSpec("gemini").ResolveUserConfigPath!(context),
                Is.EqualTo(Path.Combine(geminiHome, ".gemini", "settings.json"))
            );
            Assert.That(
                ConduitSetupWizardUtility.FindEditorSpec("github-copilot-cli").ResolveUserConfigPath!(context),
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
            var spec = ConduitSetupWizardUtility.FindEditorSpec("codex");

            Assert.That(
                ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath),
                Is.True
            );
            Assert.That(
                ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(
                    spec,
                    configPath,
                    out var configuredPath
                ),
                Is.True
            );
            Assert.That(ConduitSetupWizardUtility.PathsEqual(configuredPath, executablePath), Is.True);
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
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.3.11";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Error));
        Assert.That(button.Label, Is.EqualTo("MCP server binary is outdated but not writeable"));
        Assert.That(
            button.Hint,
            Is.EqualTo(
                $"Conduit cannot automatically update MCP server executables in this path: `{executablePath}`."
            )
        );
        Assert.That(button.IsOutdated, Is.True);
    }

    [Test]
    public void OutdatedNixOsSystemProfileExecutableDefersToNixOsConfiguration()
    {
        const string executablePath = "/run/current-system/sw/bin/conduit";
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.3.11";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButtonCore(
            ConduitSetupWizardUtility.ConfigurationLocation.User,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
        Assert.That(button.Label, Is.EqualTo("MCP server managed by NixOS"));
        Assert.That(button.Hint, Does.Contain("Update it through your NixOS configuration"));
        Assert.That(button.Hint, Does.Contain(executablePath));
        Assert.That(button.IsOutdated, Is.False);
    }

    [Test]
    public void NixOsSystemProfilePathDoesNotMatchSimilarOrStorePaths()
    {
        Assert.That(
            ConduitSetupWizardUtility.IsNixOsSystemProfilePath(
                "/run/current-system/sw/bin/conduit"
            ),
            Is.True
        );
        Assert.That(
            ConduitSetupWizardUtility.IsNixOsSystemProfilePath(
                "/run/current-system-old/sw/bin/conduit"
            ),
            Is.False
        );
        Assert.That(
            ConduitSetupWizardUtility.IsNixOsSystemProfilePath(
                "/nix/store/example-conduit/bin/conduit"
            ),
            Is.False
        );
    }

    [Test]
    public void OutdatedReadOnlyExecutableCannotBeOverwritten()
    {
        string executablePath = CreateExecutable("conduit-read-only");
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.3.12";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.3.11";
        File.SetAttributes(executablePath, File.GetAttributes(executablePath) | FileAttributes.ReadOnly);

        try
        {
            var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

            Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Error));
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
            ConduitSetupWizardUtility.IsAutomaticUpdateLocation(
                Path.Combine(homePath, ".local", "bin", "conduit"),
                homePath,
                otherProjectPath
            ),
            Is.True
        );
        Assert.That(
            ConduitSetupWizardUtility.IsAutomaticUpdateLocation(
                Path.Combine(otherProjectPath, "Conduit", "conduit"),
                homePath,
                otherProjectPath
            ),
            Is.True
        );
        Assert.That(
            ConduitSetupWizardUtility.IsAutomaticUpdateLocation(
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

    [Test]
    public void ManualSetupExtractsSelectedEditorSection()
    {
        const string markdown =
            "<details>\n<summary>Codex</summary>\nCodex instructions\n" +
            "##### http\nHTTP instructions\n##### approve tool calls\n" +
            "Approval instructions\n</details>\n" +
            "<details>\n<summary>Cursor</summary>\nCursor instructions\n</details>";

        string codexSection = ConduitManualSetupWindow.ExtractSection(markdown, "Codex")!;
        string approvalSection = ConduitManualSetupWindow.ExtractHeadingSection(
            ref codexSection,
            "approve tool calls"
        );
        string httpSection = ConduitManualSetupWindow.ExtractHeadingSection(
            ref codexSection,
            "http"
        );

        Assert.That(codexSection, Is.EqualTo("Codex instructions"));
        Assert.That(httpSection, Is.EqualTo("HTTP instructions"));
        Assert.That(approvalSection, Is.EqualTo("Approval instructions"));
        Assert.That(codexSection, Does.Not.Contain("Cursor instructions"));
        Assert.That(
            ConduitManualSetupWindow.ExtractSection(markdown, "Cursor"),
            Is.EqualTo("Cursor instructions")
        );
        Assert.That(ConduitManualSetupWindow.ExtractSection(markdown, "Missing"), Is.Null);
    }

    [Test]
    public void InlineCodeFormatterStylesPathsForSettingsHints()
    {
        string formatted = ConduitManualSetupWindow.FormatInlineCode(
            "Configuration file: `/home/example/.codex/config.toml`."
        );

        Assert.That(formatted, Does.Contain("<color="));
        Assert.That(formatted, Does.Contain("<b>/home/example/.codex/config.toml</b>"));
        Assert.That(formatted, Does.Not.Contain("`"));
    }

    [Test]
    public void ManualSetupKeepsOnlyInstructionsForTheEditorPlatform()
    {
        const string markdown = "Intro\n##### stdio | Windows (Native)\nWindows native\n" +
                                "##### stdio | Windows (WSL)\nWindows WSL\n" +
                                "##### stdio | Linux\nLinux\n";

        string linux = ConduitManualSetupWindow.SelectPlatformInstructions(
            markdown,
            RuntimePlatform.LinuxEditor
        );
        string windows = ConduitManualSetupWindow.SelectPlatformInstructions(
            markdown,
            RuntimePlatform.WindowsEditor
        );

        Assert.That(linux, Does.Contain("Intro"));
        Assert.That(linux, Does.Contain("Linux"));
        Assert.That(linux, Does.Not.Contain("Windows native"));
        Assert.That(linux, Does.Not.Contain("Windows WSL"));
        Assert.That(windows, Does.Contain("Windows native"));
        Assert.That(windows, Does.Contain("Windows WSL"));
        Assert.That(windows, Does.Not.Contain("##### stdio | Linux"));
    }

    [TestCase("stdio | Linux", "stdio (recommended)")]
    [TestCase("stdio | macOS", "stdio (recommended)")]
    [TestCase("stdio | Windows (Native)", "stdio (recommended) | native Windows")]
    [TestCase("stdio | Windows (WSL)", "stdio (recommended) | WSL")]
    [TestCase("approve tool calls", "approve tool calls")]
    public void ManualSetupUsesFriendlyDisplayHeadings(string readmeHeading, string displayHeading)
        => Assert.That(
            ConduitManualSetupWindow.GetDisplayHeading(readmeHeading),
            Is.EqualTo(displayHeading)
        );

    [Test]
    public void ManualSetupReplacesReadmeBuildPathsWithInstalledHomePaths()
    {
        const string markdown =
            "json C:\\\\src\\\\Conduit\\\\Conduit.Server\\\\publish\\\\win-x64\\\\conduit.exe\n" +
            "cli -- C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe\n" +
            "cwd = \"C:\\\\src\\\\Conduit\"\n" +
            "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe\n" +
            "cwd = /mnt/c/src/Conduit\n" +
            "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit\n" +
            "cwd = /home/you/src/Conduit\n" +
            "conduit --http --port 5080";

        string linux = ConduitManualSetupWindow.PatchExecutablePaths(
            markdown,
            RuntimePlatform.LinuxEditor,
            "/home/alice"
        );
        string windows = ConduitManualSetupWindow.PatchExecutablePaths(
            markdown,
            RuntimePlatform.WindowsEditor,
            @"C:\Users\Alice Smith"
        );

        Assert.That(linux, Does.Contain("/home/alice/.local/bin/conduit"));
        Assert.That(linux, Does.Contain("/home/alice/.local/bin/conduit --http"));
        Assert.That(linux, Does.Contain("cwd = /home/alice/.local/bin"));
        Assert.That(linux, Does.Not.Contain("/home/you/src/Conduit"));
        Assert.That(windows, Does.Contain(@"C:\\Users\\Alice Smith\\Conduit\\conduit.exe"));
        Assert.That(windows, Does.Contain("cwd = \"C:\\\\Users\\\\Alice Smith\\\\Conduit\""));
        Assert.That(windows, Does.Contain("-- \"C:\\Users\\Alice Smith\\Conduit\\conduit.exe\""));
        Assert.That(windows, Does.Contain("/mnt/c/Users/Alice Smith/Conduit/conduit.exe"));
        Assert.That(windows, Does.Contain("cwd = /mnt/c/Users/Alice Smith/Conduit"));
        Assert.That(
            windows,
            Does.Contain("& \"C:\\Users\\Alice Smith\\Conduit\\conduit.exe\" --http")
        );
    }

    [Test]
    public void EditorSpecsExcludeRemovedAutoconfiguration()
    {
        foreach (var spec in ConduitSetupWizardUtility.GetEditorSpecs())
        {
            Assert.That(spec.Id, Is.Not.EqualTo("roo-code"));
            Assert.That(spec.Id, Is.Not.EqualTo("unity-ai"));
        }
    }

    [Test]
    public void JunieAppearsAsOneEditorOption()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("rider-junie");

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
        var spec = ConduitSetupWizardUtility.FindEditorSpec(editorId);

        Assert.That(ConduitSetupWizardUtility.SupportsProjectConfiguration(spec), Is.True);
        Assert.That(spec.ResolveProjectConfigPath, Is.Not.Null);
        Assert.That(spec.ResolveUserConfigPath, Is.Not.Null);
        Assert.That(
            ConduitSetupWizardUtility.GetDefaultConfigurationLocation(spec),
            Is.EqualTo(ConduitSetupWizardUtility.ConfigurationLocation.Project)
        );
    }

    [TestCase("claude-desktop")]
    [TestCase("cline")]
    [TestCase("windsurf")]
    public void EditorSpecsWithoutProjectConfigurationDefaultToUser(string editorId)
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec(editorId);

        Assert.That(ConduitSetupWizardUtility.SupportsProjectConfiguration(spec), Is.False);
        Assert.That(spec.ResolveProjectConfigPath, Is.Null);
        Assert.That(spec.ResolveUserConfigPath, Is.Not.Null);
        Assert.That(
            ConduitSetupWizardUtility.GetDefaultConfigurationLocation(spec),
            Is.EqualTo(ConduitSetupWizardUtility.ConfigurationLocation.User)
        );
    }

    [Test]
    public void ConfigureEditorWritesOnlyTheSelectedConfigurationLocation()
    {
        var spec = CreateTempSpec("cursor", "cursor-user-scope.json");
        string projectConfigPath = Path.Combine(tempRoot, "cursor-project-scope.json");
        string userConfigPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-cursor-scope.exe");
        spec.ResolveProjectConfigPath = _ => projectConfigPath;
        using var projectScope = new FileScope(projectConfigPath);
        using var userScope = new FileScope(userConfigPath);
        DeleteConfig(projectConfigPath);
        DeleteConfig(userConfigPath);

        ConduitSetupWizardUtility.ConfigureEditor(
            spec,
            ConduitSetupWizardUtility.ConfigurationLocation.Project,
            executablePath
        );

        Assert.That(File.Exists(projectConfigPath), Is.True);
        Assert.That(File.Exists(userConfigPath), Is.False);
        Assert.That(
            ConduitSetupWizardUtility.GetDisplayConfigPath(
                spec,
                ConduitSetupWizardUtility.ConfigurationLocation.Project
            ),
            Is.EqualTo(projectConfigPath)
        );
        Assert.That(
            ConduitSetupWizardUtility.GetDisplayConfigPath(
                spec,
                ConduitSetupWizardUtility.ConfigurationLocation.User
            ),
            Is.EqualTo(userConfigPath)
        );
    }

    [Test]
    public void ExistingUserProfileConfigurationIsPreferredOverTheProjectDefault()
    {
        var spec = CreateTempSpec("cursor", "cursor-user-preferred.json");
        string userConfigPath = ConduitSetupWizardUtility.GetConfigPath(spec)!;
        string projectConfigPath = Path.Combine(tempRoot, "cursor-project-preferred.json");
        string executablePath = CreateExecutable("conduit-cursor-user-preferred.exe");
        spec.ResolveProjectConfigPath = _ => projectConfigPath;
        using var projectScope = new FileScope(projectConfigPath);
        using var userScope = new FileScope(userConfigPath);
        DeleteConfig(projectConfigPath);
        DeleteConfig(userConfigPath);
        ConduitSetupWizardUtility.ConfigureEditor(
            spec,
            ConduitSetupWizardUtility.ConfigurationLocation.User,
            executablePath
        );

        var location = ConduitSetupWizardUtility.GetPreferredConfigurationLocation(
            spec,
            ConduitSetupWizardUtility.ConfigurationLocation.Project
        );

        Assert.That(
            location,
            Is.EqualTo(ConduitSetupWizardUtility.ConfigurationLocation.User)
        );
    }

    [Test]
    public void VisualStudioAutoconfigurationIsWindowsOnly()
    {
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(
            ConduitSetupWizardUtility.FindEditorSpec("vs-copilot")
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

        Assert.That(ConduitSetupWizardUtility.HasUserConfigurationFile(spec), Is.False);

        File.WriteAllText(alternatePath, "{}");

        Assert.That(ConduitSetupWizardUtility.HasUserConfigurationFile(spec), Is.True);
    }

    [Test]
    public void UpdatedServerPathMakesConfigureButtonActionable()
    {
        var spec = CreateTempSpec("cursor", "cursor-updated.json");
        string? configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
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

        var button = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, updatedExecutablePath, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Configure Cursor"));
    }

    [Test]
    public void PrepareDestinationForOverwriteStopsCurrentPlatformExecutable()
    {
        string destinationPath = Application.platform == RuntimePlatform.WindowsEditor
            ? Path.Combine(projectRoot, "Conduit", "conduit.exe")
            : Path.Combine(projectRoot, "Conduit", "conduit");
        string stoppedPath = string.Empty;
        ConduitSetupWizardUtility.StopRunningExecutableOverride = path => stoppedPath = path;

        typeof(ConduitSetupWizardUtility)
            .GetMethod(
                "PrepareDestinationForOverwrite",
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static
            )!
            .Invoke(null, new object[] { destinationPath });

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

        ConduitSetupWizardUtility.ReplaceDownloadedFile(stagedPath, destinationPath);

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
            ConduitSetupWizardUtility.IsNixOsLinux(
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
            ConduitSetupWizardUtility.IsNixOsLinux(
                isLinux: true,
                files.ContainsKey,
                path => files[path]
            ),
            Is.False
        );
        Assert.That(
            ConduitSetupWizardUtility.IsNixOsLinux(
                isLinux: false,
                files.ContainsKey,
                path => files[path]
            ),
            Is.False
        );
        Assert.That(
            ConduitSetupWizardUtility.IsNixOsLinux(
                isLinux: true,
                _ => true,
                _ => throw new IOException("blocked")
            ),
            Is.False
        );
    }

    string CreateExecutable(string fileName)
    {
        string executablePath = Path.Combine(tempRoot, fileName);
        File.WriteAllText(executablePath, "echo conduit");
        return executablePath;
    }

    ConduitSetupWizardUtility.EditorSpec CreateTempSpec(string id, string configFileName)
    {
        var source = ConduitSetupWizardUtility.FindEditorSpec(id);
        string configPath = Path.Combine(tempRoot, configFileName);
        return new()
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            ManualSetupSection = source.ManualSetupSection,
            CreateMissingConfig = source.CreateMissingConfig,
            Format = source.Format,
            BodyPath = source.BodyPath,
            TypeValue = source.TypeValue,
            EnabledValue = source.EnabledValue,
            DisabledValue = source.DisabledValue,
            UseCommandArray = source.UseCommandArray,
            TypeOptionalWhenReading = source.TypeOptionalWhenReading,
            StateOptionalWhenReading = source.StateOptionalWhenReading,
            IncludeAllTools = source.IncludeAllTools,
            CreateOnlyConfig = source.CreateOnlyConfig,
            RequireUnambiguousConfigPath = source.RequireUnambiguousConfigPath,
            RemoveKeys = source.RemoveKeys,
            ResolveUserConfigPath = _ => configPath,
            ResolveUserConfigPaths = null,
        };
    }

    static void DeleteConfig(string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        string? directoryPath = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(directoryPath)
               && Directory.Exists(directoryPath)
               && Directory.GetFileSystemEntries(directoryPath).Length == 0)
        {
            Directory.Delete(directoryPath);
            directoryPath = Path.GetDirectoryName(directoryPath);
        }
    }

    sealed class FileScope : IDisposable
    {
        readonly string path;
        readonly string backupPath;
        readonly bool existed;

        public FileScope(string path)
        {
            this.path = path;
            backupPath = path + ".bak";
            existed = File.Exists(path);
            if (!existed)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(path, backupPath, true);
        }

        public void Dispose()
        {
            if (File.Exists(path))
                File.Delete(path);

            if (existed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(backupPath, path);
                return;
            }

            if (File.Exists(backupPath))
                File.Delete(backupPath);

            string? directoryPath = Path.GetDirectoryName(path);
            while (!string.IsNullOrWhiteSpace(directoryPath)
                   && Directory.Exists(directoryPath)
                   && Directory.GetFileSystemEntries(directoryPath).Length == 0)
            {
                Directory.Delete(directoryPath);
                directoryPath = Path.GetDirectoryName(directoryPath);
            }
        }
    }
}
