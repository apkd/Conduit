#nullable enable

using System;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        // explicit Codex approvals ensure newly added tools retain the client's normal prompt policy
        static readonly string[] codexApprovedTools =
        {
            BridgeCommandTypes.DiscardScenes,
            BridgeCommandTypes.ExecuteCode,
            BridgeCommandTypes.FindMissingScripts,
            BridgeCommandTypes.FindReferencesTo,
            BridgeCommandTypes.FromJsonOverwrite,
            BridgeCommandTypes.GetDependencies,
            BridgeCommandTypes.Help,
            BridgeCommandTypes.PlayMode,
            BridgeCommandTypes.EditMode,
            BridgeCommandTypes.ProfilerBrowse,
            BridgeCommandTypes.ProfilerOverview,
            BridgeCommandTypes.ProfilerRecord,
            BridgeCommandTypes.RefreshAssetDatabase,
            BridgeCommandTypes.ReimportAssets,
            BridgeCommandTypes.Reflect,
            BridgeCommandTypes.Restart,
            BridgeCommandTypes.RunTestsEditMode,
            BridgeCommandTypes.RunTestsPlayer,
            BridgeCommandTypes.RunTestsPlayMode,
            BridgeCommandTypes.SaveScenes,
            BridgeCommandTypes.Screenshot,
            BridgeCommandTypes.Search,
            BridgeCommandTypes.Show,
            BridgeCommandTypes.Status,
            BridgeCommandTypes.ToJson,
            BridgeCommandTypes.ViewBurstAsm,
        };

        // each spec describes the client's published schema while the shared writer preserves unrelated entries
        static readonly EditorSpec[] editorSpecs =
        {
            new()
            {
                Id = "antigravity",
                DisplayName = "Antigravity",
                ManualSetupSection = "Antigravity",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                DisabledValue = false,
                StateOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "httpUrl", "serverUrl", "headers" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".agents", "mcp_config.json"),
                ResolveUserConfigPath = static context =>
                    Combine(context.UserHome, ".gemini", "config", "mcp_config.json"),
            },
            new()
            {
                Id = "claude-code",
                DisplayName = "Claude Code",
                ManualSetupSection = "Claude Code",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context => Combine(context.ProjectRoot, ".mcp.json"),
                ResolveUserConfigPath = static context => Combine(context.UserHome, ".claude.json"),
            },
            new()
            {
                Id = "claude-desktop",
                DisplayName = "Claude Desktop",
                ManualSetupSection = "Claude Desktop",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "serverUrl", "headers" },
                ResolveUserConfigPath = ResolveClaudeDesktopConfigPath,
            },
            new()
            {
                Id = "cline",
                DisplayName = "Cline",
                ManualSetupSection = "Cline",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                DisabledValue = false,
                StateOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "serverUrl", "headers" },
                ResolveUserConfigPath = ResolveClineConfigPath,
                ResolveUserConfigPaths = ResolveClineConfigPaths,
            },
            new()
            {
                Id = "codex",
                DisplayName = "Codex",
                ManualSetupSection = "Codex",
                CreateMissingConfig = true,
                Format = ConfigFormat.Toml,
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".codex", "config.toml"),
                ResolveUserConfigPath = static context => Combine(ResolveCodexHome(context), "config.toml"),
            },
            new()
            {
                Id = "continue",
                DisplayName = "Continue",
                ManualSetupSection = "Continue",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                // an isolated Claude-compatible JSON file avoids rewriting the user's schema-v1 YAML
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".continue", "mcpServers", "unity.json"),
                ResolveUserConfigPath = static context =>
                    Combine(context.UserHome, ".continue", "mcpServers", "unity.json"),
            },
            new()
            {
                Id = "cursor",
                DisplayName = "Cursor",
                ManualSetupSection = "Cursor",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers", "auth" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".cursor", "mcp.json"),
                ResolveUserConfigPath = static context => Combine(context.UserHome, ".cursor", "mcp.json"),
            },
            new()
            {
                Id = "gemini",
                DisplayName = "Gemini CLI",
                ManualSetupSection = "Gemini CLI",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "url", "httpUrl", "headers" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".gemini", "settings.json"),
                ResolveUserConfigPath = static context =>
                    Combine(ResolveGeminiHome(context), ".gemini", "settings.json"),
            },
            new()
            {
                Id = "github-copilot-cli",
                DisplayName = "GitHub Copilot CLI",
                ManualSetupSection = "GitHub Copilot CLI",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                IncludeAllTools = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".github", "mcp.json"),
                ResolveUserConfigPath = static context =>
                    Combine(ResolveCopilotHome(context), "mcp-config.json"),
                ResolveProjectConfigPaths = static context => new[]
                {
                    Combine(context.ProjectRoot, ".github", "mcp.json"),
                    Combine(context.ProjectRoot, ".mcp.json"),
                },
            },
            new()
            {
                Id = "kilo-code",
                DisplayName = "Kilo Code",
                ManualSetupSection = "Kilo Code",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcp",
                TypeValue = "local",
                EnabledValue = true,
                StateOptionalWhenReading = true,
                UseCommandArray = true,
                RemoveKeys = new[] { "url", "headers", "oauth", "args" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".kilo", "kilo.json"),
                ResolveUserConfigPath = static context =>
                    Combine(ResolveXdgConfigHome(context), "kilo", "kilo.json"),
                ResolveProjectConfigPaths = static context => new[]
                {
                    Combine(context.ProjectRoot, ".kilo", "kilo.jsonc"),
                    Combine(context.ProjectRoot, ".kilo", "kilo.json"),
                    Combine(context.ProjectRoot, "kilo.jsonc"),
                    Combine(context.ProjectRoot, "kilo.json"),
                },
                ResolveUserConfigPaths = static context => new[]
                {
                    Combine(ResolveXdgConfigHome(context), "kilo", "kilo.jsonc"),
                    Combine(ResolveXdgConfigHome(context), "kilo", "kilo.json"),
                },
            },
            new()
            {
                Id = "open-code",
                DisplayName = "OpenCode",
                ManualSetupSection = "OpenCode",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcp",
                TypeValue = "local",
                EnabledValue = true,
                StateOptionalWhenReading = true,
                UseCommandArray = true,
                RemoveKeys = new[] { "url", "headers", "oauth", "args" },
                ResolveProjectConfigPath = static context => Combine(context.ProjectRoot, "opencode.json"),
                ResolveUserConfigPath = static context =>
                    Combine(ResolveXdgConfigHome(context), "opencode", "opencode.json"),
                ResolveProjectConfigPaths = static context => new[]
                {
                    Combine(context.ProjectRoot, "opencode.jsonc"),
                    Combine(context.ProjectRoot, "opencode.json"),
                    Combine(context.ProjectRoot, ".opencode", "opencode.jsonc"),
                    Combine(context.ProjectRoot, ".opencode", "opencode.json"),
                },
                ResolveUserConfigPaths = static context => new[]
                {
                    Combine(ResolveXdgConfigHome(context), "opencode", "opencode.jsonc"),
                    Combine(ResolveXdgConfigHome(context), "opencode", "opencode.json"),
                    Combine(ResolveXdgConfigHome(context), "opencode", "config.json"),
                },
            },
            new()
            {
                Id = "rider-junie",
                DisplayName = "Junie",
                ManualSetupSection = "JetBrains IDEs / Junie",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "type", "enabled", "disabled", "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".junie", "mcp", "mcp.json"),
                ResolveUserConfigPath = static context =>
                    Combine(context.UserHome, ".junie", "mcp", "mcp.json"),
            },
            new()
            {
                Id = "vs-copilot",
                DisplayName = "Visual Studio (Copilot)",
                ManualSetupSection = "Visual Studio / GitHub Copilot",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "servers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                        ? Combine(context.ProjectRoot, ".mcp.json")
                        : null,
                ResolveUserConfigPath = static context =>
                    UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                        ? Combine(context.UserHome, ".mcp.json")
                        : null,
                ResolveProjectConfigPaths = ResolveVisualStudioConfigPaths,
            },
            new()
            {
                Id = "vscode-copilot",
                DisplayName = "VS Code (Copilot)",
                ManualSetupSection = "VS Code / GitHub Copilot Chat",
                CreateMissingConfig = true,
                Format = ConfigFormat.Json,
                BodyPath = "servers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".vscode", "mcp.json"),
                ResolveUserConfigPath = ResolveVisualStudioCodeUserConfigPath,
            },
            new()
            {
                Id = "windsurf",
                DisplayName = "Windsurf",
                ManualSetupSection = "Windsurf",
                CreateMissingConfig = true,
                RequireUnambiguousConfigPath = true, // different Windsurf versions disagree on the active global path
                Format = ConfigFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "type", "url", "serverUrl", "headers" },
                ResolveUserConfigPath = static context =>
                    Combine(context.UserHome, ".codeium", "mcp_config.json"),
                ResolveUserConfigPaths = static context => new[]
                {
                    Combine(context.UserHome, ".codeium", "mcp_config.json"),
                    Combine(context.UserHome, ".codeium", "windsurf", "mcp_config.json"),
                },
            },
            new()
            {
                Id = "zed",
                DisplayName = "Zed",
                ManualSetupSection = "Zed",
                CreateMissingConfig = true,
                CreateOnlyConfig = true, // settings.json is JSONC in Zed, so rewriting can discard comments
                Format = ConfigFormat.Json,
                BodyPath = "context_servers",
                RemoveKeys = new[] { "type", "url", "headers", "source", "settings" },
                ResolveProjectConfigPath = static context =>
                    Combine(context.ProjectRoot, ".zed", "settings.json"),
                ResolveUserConfigPath = ResolveZedUserConfigPath,
            },
        };

        static string ResolveCodexHome(PathContext context)
            => GetEnvironmentPath("CODEX_HOME") ?? Combine(context.UserHome, ".codex");

        static string ResolveGeminiHome(PathContext context)
            => GetEnvironmentPath("GEMINI_CLI_HOME") ?? context.UserHome;

        static string ResolveCopilotHome(PathContext context)
            => GetEnvironmentPath("COPILOT_HOME") ?? Combine(context.UserHome, ".copilot");

        static string ResolveXdgConfigHome(PathContext context)
            => GetEnvironmentPath("XDG_CONFIG_HOME") ?? Combine(context.UserHome, ".config");

        static string? ResolveClaudeDesktopConfigPath(PathContext context)
            => UnityEngine.Application.platform switch
            {
                UnityEngine.RuntimePlatform.WindowsEditor =>
                    Combine(context.AppData, "Claude", "claude_desktop_config.json"),
                UnityEngine.RuntimePlatform.OSXEditor =>
                    Combine(
                        context.UserHome,
                        "Library",
                        "Application Support",
                        "Claude",
                        "claude_desktop_config.json"
                    ),
                UnityEngine.RuntimePlatform.LinuxEditor =>
                    Combine(
                        ResolveXdgConfigHome(context),
                        "Claude",
                        "claude_desktop_config.json"
                    ),
                _ => null,
            };

        static string ResolveClineConfigPath(PathContext context)
        {
            if (GetEnvironmentPath("CLINE_MCP_SETTINGS_PATH") is { } settingsPath)
                return settingsPath;
            if (GetEnvironmentPath("CLINE_DATA_DIR") is { } dataPath)
                return Combine(dataPath, "settings", "cline_mcp_settings.json");
            if (GetEnvironmentPath("CLINE_DIR") is { } clinePath)
                return Combine(clinePath, "data", "settings", "cline_mcp_settings.json");

            // the Cline extension keeps MCP state in its host's global storage, unlike its shared CLI core
            var hosts = new[]
            {
                (ExtensionDirectory: ".vscode", UserDataDirectory: "Code"),
                (ExtensionDirectory: ".vscode-insiders", UserDataDirectory: "Code - Insiders"),
                (ExtensionDirectory: ".cursor", UserDataDirectory: "Cursor"),
                (ExtensionDirectory: ".windsurf", UserDataDirectory: "Windsurf"),
            };
            foreach (var host in hosts)
                if (HasExtension(
                        new[] { Combine(context.UserHome, host.ExtensionDirectory, "extensions") },
                        "saoudrizwan.claude-dev*"
                    ))
                    return ResolveClineExtensionConfigPath(context, host.UserDataDirectory);

            return Combine(context.UserHome, ".cline", "data", "settings", "cline_mcp_settings.json");
        }

        static string?[] ResolveClineConfigPaths(PathContext context)
            => new[]
            {
                ResolveClineConfigPath(context),
                ResolveClineExtensionConfigPath(context, "Code"),
                ResolveClineExtensionConfigPath(context, "Code - Insiders"),
                ResolveClineExtensionConfigPath(context, "Cursor"),
                ResolveClineExtensionConfigPath(context, "Windsurf"),
                Combine(context.UserHome, ".cline", "data", "settings", "cline_mcp_settings.json"),
            };

        static string ResolveClineExtensionConfigPath(PathContext context, string hostDirectory)
            => Combine(
                context.AppData,
                hostDirectory,
                "User",
                "globalStorage",
                "saoudrizwan.claude-dev",
                "settings",
                "cline_mcp_settings.json"
            );

        static string?[] ResolveVisualStudioConfigPaths(PathContext context)
            => UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                ? new[]
                {
                    Combine(context.ProjectRoot, ".mcp.json"),
                    Combine(context.ProjectRoot, ".vs", "mcp.json"),
                    Combine(context.ProjectRoot, ".vscode", "mcp.json"),
                    Combine(context.ProjectRoot, ".cursor", "mcp.json"),
                }
                : Array.Empty<string?>();

        static string? ResolveVisualStudioCodeUserConfigPath(PathContext context)
            => UnityEngine.Application.platform switch
            {
                UnityEngine.RuntimePlatform.WindowsEditor =>
                    Combine(context.AppData, "Code", "User", "mcp.json"),
                UnityEngine.RuntimePlatform.OSXEditor =>
                    Combine(context.UserHome, "Library", "Application Support", "Code", "User", "mcp.json"),
                UnityEngine.RuntimePlatform.LinuxEditor =>
                    Combine(ResolveXdgConfigHome(context), "Code", "User", "mcp.json"),
                _ => null,
            };

        static string? ResolveZedUserConfigPath(PathContext context)
            => UnityEngine.Application.platform switch
            {
                UnityEngine.RuntimePlatform.WindowsEditor => Combine(context.AppData, "Zed", "settings.json"),
                UnityEngine.RuntimePlatform.OSXEditor or UnityEngine.RuntimePlatform.LinuxEditor =>
                    Combine(ResolveXdgConfigHome(context), "zed", "settings.json"),
                _ => null,
            };

        static string? GetEnvironmentPath(string variableName)
        {
            string? value = Environment.GetEnvironmentVariable(variableName)?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value))
                return null;

            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (value == "~")
                    value = home;
                else if (value!.StartsWith("~/", StringComparison.Ordinal)
                         || value.StartsWith("~\\", StringComparison.Ordinal))
                    value = Combine(home, value[2..]);

                return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            }
            catch
            {
                return null;
            }
        }

        public static EditorSpec[] GetEditorSpecs() => editorSpecs;

        public static EditorSpec FindEditorSpec(string id)
        {
            foreach (var spec in editorSpecs)
                if (spec.Id == id)
                    return spec;

            throw new InvalidOperationException($"Unsupported editor '{id}'.");
        }
    }
}
