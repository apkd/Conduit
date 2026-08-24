#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using Conduit;
using UnityEngine;

public sealed partial class ConduitSetupWizardTests
{
    [Test]
    public void ManualSetupExtractsSelectedEditorSection()
    {
        const string markdown =
            "<details>\n<summary>Codex</summary>\nCodex instructions\n" +
            "##### http\nHTTP instructions\n##### approve tool calls\n" +
            "Approval instructions\n</details>\n" +
            "<details>\n<summary>Cursor</summary>\nCursor instructions\n</details>";

        string codexSection = ConduitManualSetupInstructions.ExtractSection(markdown, "Codex")!;
        string approvalSection = ConduitManualSetupInstructions.ExtractHeadingSection(
            ref codexSection,
            "approve tool calls"
        );
        string httpSection = ConduitManualSetupInstructions.ExtractHeadingSection(
            ref codexSection,
            "http"
        );

        Assert.That(codexSection, Is.EqualTo("Codex instructions"));
        Assert.That(httpSection, Is.EqualTo("HTTP instructions"));
        Assert.That(approvalSection, Is.EqualTo("Approval instructions"));
        Assert.That(codexSection, Does.Not.Contain("Cursor instructions"));
        Assert.That(
            ConduitManualSetupInstructions.ExtractSection(markdown, "Cursor"),
            Is.EqualTo("Cursor instructions")
        );
        Assert.That(ConduitManualSetupInstructions.ExtractSection(markdown, "Missing"), Is.Null);
    }

    [Test]
    public void InlineCodeFormatterStylesPathsForSettingsHints()
    {
        string formatted = ConduitManualSetupInstructions.FormatInlineCode(
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

        string linux = ConduitManualSetupInstructions.SelectPlatformInstructions(
            markdown,
            RuntimePlatform.LinuxEditor
        );
        string windows = ConduitManualSetupInstructions.SelectPlatformInstructions(
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
            ConduitManualSetupInstructions.GetDisplayHeading(readmeHeading),
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

        string linux = ConduitManualSetupInstructions.PatchExecutablePaths(
            markdown,
            RuntimePlatform.LinuxEditor,
            "/home/alice"
        );
        string windows = ConduitManualSetupInstructions.PatchExecutablePaths(
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

}
