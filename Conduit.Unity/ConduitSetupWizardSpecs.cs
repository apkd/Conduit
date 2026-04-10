#nullable enable

using System;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        static readonly string[] codexApprovedTools =
        {
            BridgeCommandTypes.DiscardScenes,
            BridgeCommandTypes.ExecuteCode,
            BridgeCommandTypes.FindMissingScripts,
            BridgeCommandTypes.FindReferencesTo,
            BridgeCommandTypes.FromJsonOverwrite,
            BridgeCommandTypes.GetDependencies,
            "help",
            BridgeCommandTypes.Play,
            BridgeCommandTypes.RefreshAssetDatabase,
            "restart",
            BridgeCommandTypes.RunTestsEditMode,
            BridgeCommandTypes.RunTestsPlayer,
            BridgeCommandTypes.RunTestsPlayMode,
            BridgeCommandTypes.SaveScenes,
            BridgeCommandTypes.Screenshot,
            BridgeCommandTypes.Search,
            BridgeCommandTypes.Show,
            BridgeCommandTypes.Status,
            BridgeCommandTypes.ToJson,
        };

        static readonly EditorSpec[] editorSpecs =
        {
            new()
            {
                Id = "antigravity",
                DisplayName = "Antigravity",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                DisabledValue = false,
                RemoveKeys = new[] { "url", "serverUrl", "type" },
                ResolveConfigPath = static context => Combine(context.UserHome, ".gemini", "antigravity", "mcp_config.json"),
            },
            new()
            {
                Id = "claude-code",
                DisplayName = "Claude Code",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "type", "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".mcp.json"),
            },
            new()
            {
                Id = "claude-desktop",
                DisplayName = "Claude Desktop",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                    ? Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
                    : Combine(context.UserHome, "Library", "Application Support", "Claude", "claude_desktop_config.json"),
            },
            new()
            {
                Id = "cline",
                DisplayName = "Cline",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => UnityEngine.Application.platform switch
                {
                    UnityEngine.RuntimePlatform.WindowsEditor => Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                    UnityEngine.RuntimePlatform.OSXEditor => Combine(context.UserHome, "Library", "Application Support", "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                    _ => Combine(context.UserHome, ".config", "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                },
            },
            new()
            {
                Id = "codex",
                DisplayName = "Codex",
                CreateMissingConfig = false,
                Format = ConfigFormat.Toml,
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".codex", "config.toml"),
                ResolveGlobalConfigPath = static context => Combine(context.UserHome, ".codex", "config.toml"),
            },
            new()
            {
                Id = "cursor",
                DisplayName = "Cursor",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".cursor", "mcp.json"),
            },
            new()
            {
                Id = "github-copilot-cli",
                DisplayName = "GitHub Copilot CLI",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "url", "type" },
                ResolveConfigPath = static context => Combine(context.UserHome, ".copilot", "mcp-config.json"),
            },
            new()
            {
                Id = "gemini",
                DisplayName = "Gemini",
                CreateMissingConfig = false,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".gemini", "settings.json"),
            },
            new()
            {
                Id = "kilo-code",
                DisplayName = "Kilo Code",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                DisabledValue = false,
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".kilocode", "mcp.json"),
            },
            new()
            {
                Id = "open-code",
                DisplayName = "Open Code",
                CreateMissingConfig = false,
                Format = ConfigFormat.Json,
                BodyPath = "mcp",
                TypeValue = "local",
                EnabledValue = true,
                UseCommandArray = true,
                RemoveKeys = new[] { "url", "args" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, "opencode.json"),
            },
            new()
            {
                Id = "rider-junie",
                DisplayName = "Rider (Junie)",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                EnabledValue = true,
                RemoveKeys = new[] { "url", "disabled" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".junie", "mcp", "mcp.json"),
            },
            new()
            {
                Id = "unity-ai",
                DisplayName = "Unity AI",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, "UserSettings", "mcp.json"),
            },
            new()
            {
                Id = "vs-copilot",
                DisplayName = "Visual Studio (Copilot)",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "servers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".vs", "mcp.json"),
            },
            new()
            {
                Id = "vscode-copilot",
                DisplayName = "VS Code (Copilot)",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "servers",
                TypeValue = "stdio",
                RemoveKeys = new[] { "url" },
                ResolveConfigPath = static context => Combine(context.ProjectRoot, ".vscode", "mcp.json"),
            },
        };

        public static EditorSpec[] GetEditorSpecs() => editorSpecs;

        public static EditorSpec FindEditorSpec(string id)
        {
            for (var index = 0; index < editorSpecs.Length; index++)
                if (editorSpecs[index].Id == id)
                    return editorSpecs[index];

            throw new InvalidOperationException($"Unsupported editor '{id}'.");
        }
    }
}
