#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed class ConduitSetupWizardTests
{
    string projectRoot = null!;
    string tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        projectRoot = ConduitAssetPathUtility.GetProjectRootPath();
        tempRoot = Path.Combine(projectRoot, "Temp", "ConduitSetupWizardTests");
        Directory.CreateDirectory(tempRoot);
        ConduitSetupWizardUtility.ResetInstallStateForTests();
    }

    [Test]
    public void ConfigureEditor_KiloCodeCreatesConfigAndDetectsIt()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("kilo-code");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var executablePath = CreateExecutable("conduit-kilo.exe");
        using var scope = new FileScope(configPath!);
        DeleteConfig(configPath!);

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(File.Exists(configPath!), Is.True);
        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        var config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("\"mcpServers\""));
        Assert.That(config, Does.Contain("\"disabled\": false"));
        Assert.That(config, Does.Contain("\"type\": \"stdio\""));
    }

    [Test]
    public void ConfigureEditor_OpenCodeWritesCommandArray()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("open-code");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var executablePath = CreateExecutable("conduit-open-code.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(configPath!, "{\n  \"mcp\": {}\n}\n");

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        var config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("\"type\": \"local\""));
        Assert.That(config, Does.Contain("\"command\": ["));
        Assert.That(config, Does.Contain(Path.GetFileName(executablePath)));
    }

    [Test]
    public void ConfigureCodexPermissions_PreservesFileAndDetectsApprovals()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("codex");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var executablePath = CreateExecutable("conduit-codex.exe");
        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "[mcp_servers.other]\n" +
            "command = \"elsewhere\"\n\n" +
            "[mcp_servers.unity]\n" +
            "startup_timeout_sec = 15\n"
        );

        ConduitSetupWizardUtility.ConfigureEditor(spec, executablePath);
        ConduitSetupWizardUtility.ConfigureCodexPermissions();

        Assert.That(ConduitSetupWizardUtility.IsEditorConfigured(spec, configPath, executablePath), Is.True);
        Assert.That(ConduitSetupWizardUtility.HasCodexPermissions(configPath!), Is.True);

        var config = File.ReadAllText(configPath!);
        Assert.That(config, Does.Contain("[mcp_servers.other]"));
        Assert.That(config, Does.Contain("tools.play.approval_mode = \"approve\""));
        Assert.That(config, Does.Not.Contain("startup_timeout_sec = 15"));
    }

    [Test]
    public void ExistingConfigWithConduitCommandCountsAsDownloadedAndConfigured()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("kilo-code");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var executablePath = CreateExecutable("conduit.exe");
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

        Assert.That(ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(spec, configPath, out var configuredPath), Is.True);
        Assert.That(configuredPath, Is.EqualTo(executablePath));

        var downloadButton = ConduitSetupWizardUtility.EvaluateDownloadButton(string.Empty, configuredPath, false, false);
        var configureButton = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(configureButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
    }

    [Test]
    public void MissingConfiguredConduitExecutableDoesNotCountAsDownloaded()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("kilo-code");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var missingExecutablePath = Path.Combine(tempRoot, "missing-conduit.exe");
        using var scope = new FileScope(configPath!);
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

        var downloadButton = ConduitSetupWizardUtility.EvaluateDownloadButton(string.Empty, missingExecutablePath, false, false);
        var configureButton = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
        Assert.That(configureButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Disabled));
    }

    [Test]
    public void ExistingConfigWithWindowsWslPathCountsAsDownloadedAndConfigured()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
            Assert.Ignore("This scenario is only relevant on Windows.");

        var spec = ConduitSetupWizardUtility.FindEditorSpec("codex");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var windowsExecutablePath = CreateExecutable("conduit-wsl.exe");
        var driveRoot = Path.GetPathRoot(windowsExecutablePath)!;
        var driveLetter = char.ToLowerInvariant(driveRoot[0]);
        var wslExecutablePath = "/mnt/" + driveLetter + "/" + windowsExecutablePath[(driveRoot.Length)..].Replace('\\', '/');

        using var scope = new FileScope(configPath!);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath!)!);
        File.WriteAllText(
            configPath!,
            "[mcp_servers.unity]\n" +
            "command = \"" + wslExecutablePath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\n"
        );

        Assert.That(ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(spec, configPath, out var configuredPath), Is.True);
        Assert.That(Path.GetFullPath(configuredPath), Is.EqualTo(Path.GetFullPath(windowsExecutablePath)));

        var downloadButton = ConduitSetupWizardUtility.EvaluateDownloadButton(string.Empty, configuredPath, false, false);
        var configureButton = ConduitSetupWizardUtility.EvaluateConfigureButton(spec, string.Empty, false, false);

        Assert.That(downloadButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(configureButton.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
    }

    [Test]
    public void OlderExecutableVersionTurnsDownloadIntoUpdate()
    {
        var executablePath = CreateExecutable(Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit");
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.2.8";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.2.7+sha.old";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Enabled));
        Assert.That(button.Label, Is.EqualTo("Update MCP Server"));
        Assert.That(button.Hint, Does.Contain("Installed server 0.2.7+sha.old is older than package version 0.2.8."));
    }

    [Test]
    public void MatchingExecutableVersionKeepsDownloadedState()
    {
        var executablePath = CreateExecutable(Application.platform == RuntimePlatform.WindowsEditor ? "conduit.exe" : "conduit");
        ConduitSetupWizardUtility.GetCurrentPackageVersionOverride = static () => "0.2.8";
        ConduitSetupWizardUtility.ProbeExecutableVersionOverride = static _ => "0.2.8+sha.current";

        var button = ConduitSetupWizardUtility.EvaluateDownloadButton(executablePath, string.Empty, false, false);

        Assert.That(button.State, Is.EqualTo(ConduitSetupWizardUtility.ActionState.Success));
        Assert.That(button.Label, Is.EqualTo("MCP Server Downloaded"));
    }

    [Test]
    public void UpdatedServerPathMakesConfigureButtonActionable()
    {
        var spec = ConduitSetupWizardUtility.FindEditorSpec("kilo-code");
        var configPath = ConduitSetupWizardUtility.GetConfigPath(spec);
        Assert.That(configPath, Is.Not.Null);

        var configuredExecutablePath = CreateExecutable("conduit-configured.exe");
        var updatedExecutablePath = CreateExecutable("conduit-updated.exe");
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
        Assert.That(button.Label, Is.EqualTo("Configure Kilo Code"));
    }

    [Test]
    public void PrepareDestinationForOverwriteStopsCurrentPlatformExecutable()
    {
        var destinationPath = Application.platform == RuntimePlatform.WindowsEditor
            ? Path.Combine(projectRoot, "Conduit", "conduit.exe")
            : Path.Combine(projectRoot, "Conduit", "conduit");
        var stoppedPath = string.Empty;
        ConduitSetupWizardUtility.StopRunningExecutableOverride = path => stoppedPath = path;

        typeof(ConduitSetupWizardUtility)
            .GetMethod("PrepareDestinationForOverwrite", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { destinationPath });

        Assert.That(stoppedPath, Is.EqualTo(Path.GetFullPath(destinationPath)));
    }

    string CreateExecutable(string fileName)
    {
        var executablePath = Path.Combine(tempRoot, fileName);
        File.WriteAllText(executablePath, "echo conduit");
        return executablePath;
    }

    static void DeleteConfig(string path)
    {
        if (File.Exists(path))
            File.Delete(path);

        var directoryPath = Path.GetDirectoryName(path);
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

            var directoryPath = Path.GetDirectoryName(path);
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
