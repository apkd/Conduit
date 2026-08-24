#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    [Test]
    public void ConfigureEditor_KiloCodeCreatesConfigAndDetectsIt()
    {
        var spec = CreateTempSpec("kilo-code", "kilo.json");
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-kilo.exe");
        using var scope = new FileScope(configPath!);
        DeleteConfig(configPath!);

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(File.Exists(configPath!), Is.True);
        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
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
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-open-code.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(configPath!, "{\n  \"mcp\": {}\n}\n");

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        string config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("\"type\": \"local\""));
        Assert.That(config, Does.Contain("\"command\": ["));
        Assert.That(config, Does.Contain(Path.GetFileName(executablePath)));
    }

    [Test]
    public void ConfigureButton_PrefersTheEditorsEffectiveExistingConfig()
    {
        var spec = CreateTempSpec("open-code", "opencode-create-target.json");
        string createTarget = EditorConfigurationPaths.GetConfigPath(spec)!;
        string existingTarget = Path.Combine(tempRoot, "opencode-existing.jsonc");
        string executablePath = CreateExecutable("conduit-opencode-existing.exe");
        spec.ResolveUserConfigPaths = _ => new[] { existingTarget };
        File.WriteAllText(existingTarget, "{\"mcp\":{}}\n");
        if (File.Exists(createTarget))
            File.Delete(createTarget);

        var button = SetupActionEvaluator.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("JSONC"));
        Assert.That(File.Exists(createTarget), Is.False);
    }

    [Test]
    public void ConfigureButton_RejectsAmbiguousWindsurfConfigPaths()
    {
        var spec = CreateTempSpec("windsurf", "windsurf-primary.json");
        string primaryPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string alternatePath = Path.Combine(tempRoot, "windsurf-alternate.json");
        string executablePath = CreateExecutable("conduit-windsurf-ambiguous.exe");
        spec.ResolveUserConfigPaths = _ => new[] { primaryPath, alternatePath };
        File.WriteAllText(primaryPath, "{}");
        File.WriteAllText(alternatePath, "{}");

        var button = SetupActionEvaluator.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("More than one Windsurf MCP config file"));
        Assert.Throws<InvalidOperationException>(
            () => EditorConfiguration.ConfigureEditor(spec, executablePath)
        );
    }

    [Test]
    public void ConfigureEditor_ClineWritesCurrentFlatSchema()
    {
        var spec = CreateTempSpec("cline", "cline_mcp_settings.json");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
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

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(
            EditorConfiguration.TryGetConfiguredExecutablePath(spec, configPath, out var configuredPath),
            Is.True
        );
        Assert.That(configuredPath, Is.EqualTo(executablePath));

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
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
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable($"conduit-{editorId}.exe");
        DeleteConfig(configPath);

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        string config = File.ReadAllText(configPath);
        Assert.That(config, Does.Contain(expectedBody));
        Assert.That(config, Does.Contain(expectedSetting));
    }

    [TestCaseSource(nameof(jsonEditorIds))]
    public void ConfigureEditor_EveryJsonTargetRoundTrips(string editorId)
    {
        var spec = CreateTempSpec(editorId, $"{editorId}-roundtrip.json");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable($"conduit-{editorId}-roundtrip.exe");
        if (spec.CreateOnlyConfig)
            DeleteConfig(configPath);
        else
            File.WriteAllText(configPath, "{\"unrelated\":{\"value\":7}}\n");

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        if (!spec.CreateOnlyConfig)
            Assert.That(File.ReadAllText(configPath), Does.Contain("\"unrelated\""));
    }

    [Test]
    public void ConfigureEditor_CopilotCliIncludesRequiredToolWildcard()
    {
        var spec = CreateTempSpec("github-copilot-cli", "copilot-mcp.json");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-copilot.exe");
        DeleteConfig(configPath);

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(File.ReadAllText(configPath), Does.Contain("\"tools\": [\n        \"*\""));
    }

    [Test]
    public void ConfigureEditor_CodexCreatesMissingConfig()
    {
        var spec = CreateTempSpec("codex", "codex-missing.toml");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-codex-missing.exe");
        DeleteConfig(configPath);

        EditorConfiguration.ConfigureEditor(spec, executablePath);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(File.ReadAllText(configPath), Does.Contain("[mcp_servers.unity]"));
    }

    [Test]
    public void ConfigureCodexPermissions_PreservesFileAndDetectsApprovals()
    {
        var spec = CreateTempSpec("codex", "codex-config.toml");
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
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

        EditorConfiguration.ConfigureEditor(spec, executablePath);
        EditorConfiguration.ConfigureCodexPermissions(configPath!);

        Assert.That(EditorConfiguration.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(CodexTomlConfiguration.HasToolPermissions(configPath!), Is.True);

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
            EditorConfiguration.IsEditorConfigured(
                EditorClientCatalog.FindEditorSpec("cursor"),
                cursorPath,
                executablePath
            ),
            Is.True
        );
        Assert.That(
            EditorConfiguration.IsEditorConfigured(
                EditorClientCatalog.FindEditorSpec("kilo-code"),
                kiloPath,
                executablePath
            ),
            Is.True
        );
        Assert.That(
            EditorConfiguration.IsEditorConfigured(
                EditorClientCatalog.FindEditorSpec("codex"),
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
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-antigravity-disabled.exe");
        File.WriteAllText(
            configPath,
            "{\"mcpServers\":{\"unity\":{\"command\":\"" +
            executablePath.Replace("\\", "\\\\") +
            "\",\"args\":[],\"disabled\":true}}}"
        );

        var button = SetupActionEvaluator.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Enabled));
    }

    [Test]
    public void ExistingZedSettingsAreLeftForLosslessManualEditing()
    {
        var spec = CreateTempSpec("zed", "zed-existing.json");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-zed-existing.exe");
        const string existingSettings = "{\n  // keep this comment\n  \"theme\": \"One Dark\"\n}\n";
        File.WriteAllText(configPath, existingSettings);

        var button = SetupActionEvaluator.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("will not rewrite"));
        Assert.Throws<InvalidOperationException>(
            () => EditorConfiguration.ConfigureEditor(spec, executablePath)
        );
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(existingSettings));
    }

    [Test]
    public void ExistingJsoncConfigCanBeDetectedWithoutRewritingIt()
    {
        var spec = CreateTempSpec("kilo-code", "kilo-existing.jsonc");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
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

        var button = SetupActionEvaluator.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Success));
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(existingConfig));
    }

    [Test]
    public void ExistingCommentedJsonIsLeftForLosslessManualEditing()
    {
        var spec = CreateTempSpec("vscode-copilot", "vscode-commented.json");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
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
        var button = SetupActionEvaluator.EvaluateConfigureButton(
            spec,
            executablePath,
            false,
            false
        );

        Assert.That(button.State, Is.EqualTo(SetupActionState.Disabled));
        Assert.That(button.Hint, Does.Contain("contains comments"));
        Assert.Throws<InvalidOperationException>(
            () => EditorConfiguration.ConfigureEditor(spec, executablePath)
        );
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(existingConfig));
    }

    [Test]
    public void ConfigureEditor_DoesNotOverwriteMalformedConfigStructure()
    {
        var spec = CreateTempSpec("cursor", "cursor-malformed.json");
        string configPath = EditorConfigurationPaths.GetConfigPath(spec)!;
        string executablePath = CreateExecutable("conduit-cursor-malformed.exe");
        const string malformedConfig = "{\"theme\":\"dark\",\"mcpServers\":[]}";
        File.WriteAllText(configPath, malformedConfig);

        Assert.Throws<InvalidOperationException>(
            () => EditorConfiguration.ConfigureEditor(spec, executablePath)
        );
        Assert.That(File.ReadAllText(configPath), Is.EqualTo(malformedConfig));
    }

    [Test]
    public void ExistingConfigWithConduitCommandCountsAsDownloadedAndConfigured()
    {
        var spec = CreateTempSpec("cursor", "cursor-existing.json");
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
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
            EditorConfiguration.TryGetConfiguredExecutablePath(
                spec,
                configPath,
                out var configuredPath
            ),
            Is.True
        );
        Assert.That(configuredPath, Is.EqualTo(executablePath));

        var downloadButton = SetupActionEvaluator.EvaluateDownloadButton(
            string.Empty,
            configuredPath,
            false,
            false
        );
        var configureButton = SetupActionEvaluator.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(SetupActionState.Success));
        Assert.That(configureButton.State, Is.EqualTo(SetupActionState.Success));
    }

    [Test]
    public void DiscoveryScansEveryKnownEditorConfigForAnExistingExecutable()
    {
        var spec = CreateTempSpec("cursor", "cursor-discovery.json");
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        string executablePath = CreateExecutable("conduit-discovered");
        string projectConfigPath = Path.Combine(tempRoot, "cursor-project-discovery.json");
        spec.ResolveProjectConfigPath = _ => projectConfigPath;
        using var scope = new FileScope(configPath!);
        using var projectScope = new FileScope(projectConfigPath);
        DeleteConfig(configPath!);
        DeleteConfig(projectConfigPath);
        EditorConfiguration.ConfigureEditor(
            spec,
            SetupConfigurationLocation.User,
            executablePath
        );

        bool found = EditorConfiguration.TryGetAnyConfiguredExecutablePath(
            new[] { spec },
            out var discoveredExecutablePath,
            out var discoveredConfigPath
        );

        Assert.That(found, Is.True);
        Assert.That(discoveredExecutablePath, Is.EqualTo(executablePath));
        Assert.That(discoveredConfigPath, Is.EqualTo(configPath));
        Assert.That(
            EditorConfiguration.TryGetAnyConfiguredExecutablePath(
                new[] { spec },
                SetupConfigurationLocation.User,
                out _,
                out _
            ),
            Is.True
        );
        Assert.That(
            EditorConfiguration.TryGetAnyConfiguredExecutablePath(
                new[] { spec },
                SetupConfigurationLocation.Project,
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
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
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

        Assert.That(EditorConfiguration.TryGetConfiguredExecutablePath(spec, configPath, out _), Is.False);

        var downloadButton = SetupActionEvaluator.EvaluateDownloadButton(
            string.Empty,
            missingExecutablePath,
            false,
            false
        );
        var configureButton = SetupActionEvaluator.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(SetupActionState.Enabled));
        Assert.That(configureButton.State, Is.EqualTo(SetupActionState.Disabled));
    }

    [Test]
    public void ExistingConfigWithWindowsWslPathCountsAsDownloadedAndConfigured()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
            Assert.Ignore("This scenario is only relevant on Windows.");

        var spec = CreateTempSpec("codex", "codex-wsl.toml");
        string? configPath = EditorConfigurationPaths.GetConfigPath(spec);
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
            EditorConfiguration.TryGetConfiguredExecutablePath(
                spec,
                configPath,
                out var configuredPath
            ),
            Is.True
        );
        Assert.That(Path.GetFullPath(configuredPath), Is.EqualTo(Path.GetFullPath(windowsExecutablePath)));

        var downloadButton = SetupActionEvaluator.EvaluateDownloadButton(
            string.Empty,
            configuredPath,
            false,
            false
        );
        var configureButton = SetupActionEvaluator.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(SetupActionState.Success));
        Assert.That(configureButton.State, Is.EqualTo(SetupActionState.Success));
    }

}
