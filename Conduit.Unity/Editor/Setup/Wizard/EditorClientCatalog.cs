#nullable enable

using System;

namespace Conduit
{
    static class EditorClientCatalog
    {
        // each spec describes the client's published schema while the shared writer preserves unrelated entries
        static readonly EditorClientSpec[] editorSpecs =
        {
            new()
            {
                Id = "antigravity",
                DisplayName = "Antigravity",
                ManualSetupSection = "Antigravity",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                DisabledValue = false,
                StateOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "httpUrl", "serverUrl", "headers" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".agents", "mcp_config.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(context.UserHome, ".gemini", "config", "mcp_config.json"),
            },
            new()
            {
                Id = "claude-code",
                DisplayName = "Claude Code",
                ManualSetupSection = "Claude Code",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context => SetupPathUtility.Combine(context.ProjectRoot, ".mcp.json"),
                ResolveUserConfigPath = static context => SetupPathUtility.Combine(context.UserHome, ".claude.json"),
            },
            new()
            {
                Id = "claude-desktop",
                DisplayName = "Claude Desktop",
                ManualSetupSection = "Claude Desktop",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
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
                Format = EditorConfigurationFormat.Json,
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
                Format = EditorConfigurationFormat.Toml,
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".codex", "config.toml"),
                ResolveUserConfigPath = static context => SetupPathUtility.Combine(ResolveCodexHome(context), "config.toml"),
            },
            new()
            {
                Id = "continue",
                DisplayName = "Continue",
                ManualSetupSection = "Continue",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                // an isolated Claude-compatible JSON file avoids rewriting the user's schema-v1 YAML
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".continue", "mcpServers", "unity.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(context.UserHome, ".continue", "mcpServers", "unity.json"),
            },
            new()
            {
                Id = "cursor",
                DisplayName = "Cursor",
                ManualSetupSection = "Cursor",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers", "auth" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".cursor", "mcp.json"),
                ResolveUserConfigPath = static context => SetupPathUtility.Combine(context.UserHome, ".cursor", "mcp.json"),
            },
            new()
            {
                Id = "gemini",
                DisplayName = "Gemini CLI",
                ManualSetupSection = "Gemini CLI",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "url", "httpUrl", "headers" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".gemini", "settings.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(ResolveGeminiHome(context), ".gemini", "settings.json"),
            },
            new()
            {
                Id = "github-copilot-cli",
                DisplayName = "GitHub Copilot CLI",
                ManualSetupSection = "GitHub Copilot CLI",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                IncludeAllTools = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".github", "mcp.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(ResolveCopilotHome(context), "mcp-config.json"),
                ResolveProjectConfigPaths = static context => new[]
                {
                    SetupPathUtility.Combine(context.ProjectRoot, ".github", "mcp.json"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".mcp.json"),
                },
            },
            new()
            {
                Id = "kilo-code",
                DisplayName = "Kilo Code",
                ManualSetupSection = "Kilo Code",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcp",
                TypeValue = "local",
                EnabledValue = true,
                StateOptionalWhenReading = true,
                UseCommandArray = true,
                RemoveKeys = new[] { "url", "headers", "oauth", "args" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".kilo", "kilo.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "kilo", "kilo.json"),
                ResolveProjectConfigPaths = static context => new[]
                {
                    SetupPathUtility.Combine(context.ProjectRoot, ".kilo", "kilo.jsonc"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".kilo", "kilo.json"),
                    SetupPathUtility.Combine(context.ProjectRoot, "kilo.jsonc"),
                    SetupPathUtility.Combine(context.ProjectRoot, "kilo.json"),
                },
                ResolveUserConfigPaths = static context => new[]
                {
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "kilo", "kilo.jsonc"),
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "kilo", "kilo.json"),
                },
            },
            new()
            {
                Id = "open-code",
                DisplayName = "OpenCode",
                ManualSetupSection = "OpenCode",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcp",
                TypeValue = "local",
                EnabledValue = true,
                StateOptionalWhenReading = true,
                UseCommandArray = true,
                RemoveKeys = new[] { "url", "headers", "oauth", "args" },
                ResolveProjectConfigPath = static context => SetupPathUtility.Combine(context.ProjectRoot, "opencode.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "opencode", "opencode.json"),
                ResolveProjectConfigPaths = static context => new[]
                {
                    SetupPathUtility.Combine(context.ProjectRoot, "opencode.jsonc"),
                    SetupPathUtility.Combine(context.ProjectRoot, "opencode.json"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".opencode", "opencode.jsonc"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".opencode", "opencode.json"),
                },
                ResolveUserConfigPaths = static context => new[]
                {
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "opencode", "opencode.jsonc"),
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "opencode", "opencode.json"),
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "opencode", "config.json"),
                },
            },
            new()
            {
                Id = "rider-junie",
                DisplayName = "Junie",
                ManualSetupSection = "JetBrains IDEs / Junie",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "type", "enabled", "disabled", "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".junie", "mcp", "mcp.json"),
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(context.UserHome, ".junie", "mcp", "mcp.json"),
            },
            new()
            {
                Id = "vs-copilot",
                DisplayName = "Visual Studio (Copilot)",
                ManualSetupSection = "Visual Studio / GitHub Copilot",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "servers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                        ? SetupPathUtility.Combine(context.ProjectRoot, ".mcp.json")
                        : null,
                ResolveUserConfigPath = static context =>
                    UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                        ? SetupPathUtility.Combine(context.UserHome, ".mcp.json")
                        : null,
                ResolveProjectConfigPaths = ResolveVisualStudioConfigPaths,
            },
            new()
            {
                Id = "vscode-copilot",
                DisplayName = "VS Code (Copilot)",
                ManualSetupSection = "VS Code / GitHub Copilot Chat",
                CreateMissingConfig = true,
                Format = EditorConfigurationFormat.Json,
                BodyPath = "servers",
                TypeValue = "stdio",
                TypeOptionalWhenReading = true,
                RemoveKeys = new[] { "url", "headers" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".vscode", "mcp.json"),
                ResolveUserConfigPath = ResolveVisualStudioCodeUserConfigPath,
            },
            new()
            {
                Id = "windsurf",
                DisplayName = "Windsurf",
                ManualSetupSection = "Windsurf",
                CreateMissingConfig = true,
                RequireUnambiguousConfigPath = true, // different Windsurf versions disagree on the active global path
                Format = EditorConfigurationFormat.Json,
                BodyPath = "mcpServers",
                RemoveKeys = new[] { "type", "url", "serverUrl", "headers" },
                ResolveUserConfigPath = static context =>
                    SetupPathUtility.Combine(context.UserHome, ".codeium", "mcp_config.json"),
                ResolveUserConfigPaths = static context => new[]
                {
                    SetupPathUtility.Combine(context.UserHome, ".codeium", "mcp_config.json"),
                    SetupPathUtility.Combine(context.UserHome, ".codeium", "windsurf", "mcp_config.json"),
                },
            },
            new()
            {
                Id = "zed",
                DisplayName = "Zed",
                ManualSetupSection = "Zed",
                CreateMissingConfig = true,
                CreateOnlyConfig = true, // settings.json is JSONC in Zed, so rewriting can discard comments
                Format = EditorConfigurationFormat.Json,
                BodyPath = "context_servers",
                RemoveKeys = new[] { "type", "url", "headers", "source", "settings" },
                ResolveProjectConfigPath = static context =>
                    SetupPathUtility.Combine(context.ProjectRoot, ".zed", "settings.json"),
                ResolveUserConfigPath = ResolveZedUserConfigPath,
            },
        };

        internal static EditorClientSpec[] GetEditorSpecs() => editorSpecs;

        internal static EditorClientSpec FindEditorSpec(string id)
        {
            foreach (var spec in editorSpecs)
                if (spec.Id == id)
                    return spec;

            throw new InvalidOperationException($"Unsupported editor '{id}'.");
        }

        static string ResolveCodexHome(SetupPathContext context)
            => GetEnvironmentPath("CODEX_HOME") ?? SetupPathUtility.Combine(context.UserHome, ".codex");

        static string ResolveGeminiHome(SetupPathContext context)
            => GetEnvironmentPath("GEMINI_CLI_HOME") ?? context.UserHome;

        static string ResolveCopilotHome(SetupPathContext context)
            => GetEnvironmentPath("COPILOT_HOME") ?? SetupPathUtility.Combine(context.UserHome, ".copilot");

        static string ResolveXdgConfigHome(SetupPathContext context)
            => GetEnvironmentPath("XDG_CONFIG_HOME") ?? SetupPathUtility.Combine(context.UserHome, ".config");

        static string? ResolveClaudeDesktopConfigPath(SetupPathContext context)
            => UnityEngine.Application.platform switch
            {
                UnityEngine.RuntimePlatform.WindowsEditor =>
                    SetupPathUtility.Combine(context.AppData, "Claude", "claude_desktop_config.json"),
                UnityEngine.RuntimePlatform.OSXEditor =>
                    SetupPathUtility.Combine(
                        context.UserHome,
                        "Library",
                        "Application Support",
                        "Claude",
                        "claude_desktop_config.json"
                    ),
                UnityEngine.RuntimePlatform.LinuxEditor =>
                    SetupPathUtility.Combine(
                        ResolveXdgConfigHome(context),
                        "Claude",
                        "claude_desktop_config.json"
                    ),
                _ => null,
            };

        static string ResolveClineConfigPath(SetupPathContext context)
        {
            if (GetEnvironmentPath("CLINE_MCP_SETTINGS_PATH") is { } settingsPath)
                return settingsPath;
            if (GetEnvironmentPath("CLINE_DATA_DIR") is { } dataPath)
                return SetupPathUtility.Combine(dataPath, "settings", "cline_mcp_settings.json");
            if (GetEnvironmentPath("CLINE_DIR") is { } clinePath)
                return SetupPathUtility.Combine(clinePath, "data", "settings", "cline_mcp_settings.json");

            // the Cline extension keeps MCP state in its host's global storage, unlike its shared CLI core
            var hosts = new[]
            {
                (ExtensionDirectory: ".vscode", UserDataDirectory: "Code"),
                (ExtensionDirectory: ".vscode-insiders", UserDataDirectory: "Code - Insiders"),
                (ExtensionDirectory: ".cursor", UserDataDirectory: "Cursor"),
                (ExtensionDirectory: ".windsurf", UserDataDirectory: "Windsurf"),
            };
            foreach (var host in hosts)
                if (ServerExecutableLocator.HasExtension(
                        new[] { SetupPathUtility.Combine(context.UserHome, host.ExtensionDirectory, "extensions") },
                        "saoudrizwan.claude-dev*"
                    ))
                    return ResolveClineExtensionConfigPath(context, host.UserDataDirectory);

            return SetupPathUtility.Combine(context.UserHome, ".cline", "data", "settings", "cline_mcp_settings.json");
        }

        static string?[] ResolveClineConfigPaths(SetupPathContext context)
            => new[]
            {
                ResolveClineConfigPath(context),
                ResolveClineExtensionConfigPath(context, "Code"),
                ResolveClineExtensionConfigPath(context, "Code - Insiders"),
                ResolveClineExtensionConfigPath(context, "Cursor"),
                ResolveClineExtensionConfigPath(context, "Windsurf"),
                SetupPathUtility.Combine(context.UserHome, ".cline", "data", "settings", "cline_mcp_settings.json"),
            };

        static string ResolveClineExtensionConfigPath(SetupPathContext context, string hostDirectory)
            => SetupPathUtility.Combine(
                context.AppData,
                hostDirectory,
                "User",
                "globalStorage",
                "saoudrizwan.claude-dev",
                "settings",
                "cline_mcp_settings.json"
            );

        static string?[] ResolveVisualStudioConfigPaths(SetupPathContext context)
            => UnityEngine.Application.platform == UnityEngine.RuntimePlatform.WindowsEditor
                ? new[]
                {
                    SetupPathUtility.Combine(context.ProjectRoot, ".mcp.json"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".vs", "mcp.json"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".vscode", "mcp.json"),
                    SetupPathUtility.Combine(context.ProjectRoot, ".cursor", "mcp.json"),
                }
                : Array.Empty<string?>();

        static string? ResolveVisualStudioCodeUserConfigPath(SetupPathContext context)
            => UnityEngine.Application.platform switch
            {
                UnityEngine.RuntimePlatform.WindowsEditor =>
                    SetupPathUtility.Combine(context.AppData, "Code", "User", "mcp.json"),
                UnityEngine.RuntimePlatform.OSXEditor =>
                    SetupPathUtility.Combine(context.UserHome, "Library", "Application Support", "Code", "User", "mcp.json"),
                UnityEngine.RuntimePlatform.LinuxEditor =>
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "Code", "User", "mcp.json"),
                _ => null,
            };

        static string? ResolveZedUserConfigPath(SetupPathContext context)
            => UnityEngine.Application.platform switch
            {
                UnityEngine.RuntimePlatform.WindowsEditor => SetupPathUtility.Combine(context.AppData, "Zed", "settings.json"),
                UnityEngine.RuntimePlatform.OSXEditor or UnityEngine.RuntimePlatform.LinuxEditor =>
                    SetupPathUtility.Combine(ResolveXdgConfigHome(context), "zed", "settings.json"),
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
                    value = SetupPathUtility.Combine(home, value[2..]);

                return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            }
            catch
            {
                return null;
            }
        }
    }
}
