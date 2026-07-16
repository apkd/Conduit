#nullable enable

using System;
using System.IO;
using System.Linq;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        public static string DetectInstalledEditorId()
        {
            string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86Path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vscodeExtensionsPaths = new[]
            {
                Combine(homePath, ".vscode", "extensions"),
                Combine(homePath, ".vscode-insiders", "extensions"),
                Combine(homePath, ".cursor", "extensions"),
                Combine(homePath, ".windsurf", "extensions"),
            };
            var jetBrainsPluginRoots = new[]
            {
                Combine(appDataPath, "JetBrains"),
                Combine(homePath, ".local", "share", "JetBrains"),
            };

            // first match wins; dedicated agents precede generic host editors commonly installed beside them
            if (FindOnPath("codex", "codex.cmd", "codex.exe") is not null
                || HasExtension(vscodeExtensionsPaths, "openai.chatgpt*")
                || HasExtension(vscodeExtensionsPaths, "openai.codex*"))
                return "codex";

            if (FindOnPath("cursor", "cursor.cmd", "cursor.exe") is not null
                || File.Exists(Combine(localAppDataPath, "Programs", "Cursor", "Cursor.exe"))
                || File.Exists(@"C:\Program Files\Cursor\Cursor.exe"))
                return "cursor";

            if (FindOnPath("opencode", "opencode.cmd", "opencode.exe") is not null
                || File.Exists(Combine(appDataPath, "npm", "opencode.cmd"))
                || HasExtension(vscodeExtensionsPaths, "sst-dev.opencode*"))
                return "open-code";

            if (FindOnPath("claude", "claude.cmd", "claude.exe") is not null
                || HasExtension(vscodeExtensionsPaths, "anthropic.claude-code*"))
                return "claude-code";

            if (FindOnPath("gemini", "gemini.cmd", "gemini.exe") is not null
                || File.Exists(Combine(appDataPath, "npm", "gemini.cmd")))
                return "gemini";

            if (FindOnPath("agy", "agy.cmd", "agy.exe") is not null
                || HasStartMenuShortcut("Antigravity")
                || File.Exists(Combine(localAppDataPath, "Programs", "Antigravity", "Antigravity.exe")))
                return "antigravity";

            if (FindOnPath("junie", "junie.cmd", "junie.exe") is not null
                || Directory.Exists(Combine(homePath, ".junie"))
                || HasJetBrainsPlugin(jetBrainsPluginRoots, "junie"))
                return "rider-junie";

            if (FindOnPath("cline", "cline.cmd", "cline.exe") is not null
                || File.Exists(Combine(appDataPath, "npm", "cline.cmd"))
                || HasExtension(vscodeExtensionsPaths, "saoudrizwan.claude-dev*"))
                return "cline";

            if (HasStartMenuShortcut("Claude")
                || File.Exists(Combine(localAppDataPath, "Programs", "Claude", "Claude.exe"))
                || File.Exists(@"C:\Program Files\Claude\Claude.exe")
                || Directory.Exists("/Applications/Claude.app")
                || Directory.Exists(Combine(homePath, "Applications", "Claude.app"))
                || FindOnPath("claude-desktop", "claude-desktop.exe") is not null
                || (ResolveClaudeDesktopConfigPath(CreatePathContext()) is { } claudeConfigPath
                    && File.Exists(claudeConfigPath)))
                return "claude-desktop";

            if (FindOnPath("copilot", "copilot.cmd", "copilot.exe") is not null
                || File.Exists(Combine(appDataPath, "npm", "copilot.cmd")))
                return "github-copilot-cli";

            if (FindOnPath("kilo", "kilo.cmd", "kilo.exe") is not null
                || HasExtension(vscodeExtensionsPaths, "kilocode.kilo-code*"))
                return "kilo-code";

            if (HasExtension(vscodeExtensionsPaths, "continue.continue*")
                || HasJetBrainsPlugin(jetBrainsPluginRoots, "continue"))
                return "continue";

            if (FindOnPath("windsurf", "windsurf.cmd", "windsurf.exe") is not null
                || File.Exists(Combine(localAppDataPath, "Programs", "Windsurf", "Windsurf.exe"))
                || Directory.Exists("/Applications/Windsurf.app"))
                return "windsurf";

            if (FindOnPath("zed", "zed.cmd", "zed.exe") is not null
                || Directory.Exists("/Applications/Zed.app"))
                return "zed";

            if (FindOnPath("code", "code.cmd", "code.exe") is not null
                || File.Exists(Combine(localAppDataPath, "Programs", "Microsoft VS Code", "Code.exe"))
                || File.Exists(Combine(programFilesPath, "Microsoft VS Code", "Code.exe")))
                return "vscode-copilot";

            if (File.Exists(Combine(programFilesX86Path, "Microsoft Visual Studio", "Installer", "vswhere.exe"))
                || File.Exists(@"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe")
                || File.Exists(@"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\devenv.exe")
                || File.Exists(@"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\devenv.exe"))
                return "vs-copilot";

            return string.Empty;
        }

        internal static bool TryFindServerExecutableOnPath(out string executablePath)
        {
            executablePath = FindOnPath("conduit", "conduit.exe") ?? string.Empty;
            return executablePath.Length > 0;
        }

        internal static string? FindOnPath(params string[] names)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            return FindOnPathValue(path, names);
        }

        internal static string? FindOnPathValue(string? path, params string[] names)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // scan PATH directly instead of depending on platform-specific where/which commands or shell setup
            var directories = path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (string pathEntry in directories)
            {
                string directory = Environment.ExpandEnvironmentVariables(
                    pathEntry.Trim().Trim('"')
                );
                foreach (string name in names)
                {
                    try
                    {
                        string fullPath = Path.Combine(directory, name);
                        if (File.Exists(fullPath))
                            return Path.GetFullPath(fullPath);
                    }
                    catch { }
                }
            }

            return null;
        }

        static bool HasStartMenuShortcut(string containsName)
        {
            if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor)
                return false;

            var roots = new[]
            {
                Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft",
                    "Windows",
                    "Start Menu",
                    "Programs"
                ),
                Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Microsoft",
                    "Windows",
                    "Start Menu",
                    "Programs"
                ),
            };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                try
                {
                    if (Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                        .Any(file => Path.GetFileNameWithoutExtension(file)
                            .Contains(containsName, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { }
            }

            return false;
        }

        static bool HasExtension(string[] extensionPaths, string searchPattern)
        {
            string prefix = searchPattern.TrimEnd('*');
            foreach (string extensionsPath in extensionPaths)
            {
                if (!Directory.Exists(extensionsPath))
                    continue;

                try
                {
                    if (Directory.EnumerateDirectories(extensionsPath)
                        .Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { }
            }

            return false;
        }

        static bool HasJetBrainsPlugin(string[] pluginRoots, string pluginPrefix)
        {
            foreach (string root in pluginRoots)
            {
                if (!Directory.Exists(root))
                    continue;

                try
                {
                    foreach (string productDirectory in Directory.EnumerateDirectories(root))
                    {
                        string pluginsDirectory = Path.Combine(productDirectory, "plugins");
                        if (!Directory.Exists(pluginsDirectory))
                            continue;

                        if (Directory.EnumerateDirectories(pluginsDirectory)
                            .Any(path => Path.GetFileName(path)
                                .StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase)))
                            return true;
                    }
                }
                catch { }
            }

            return false;
        }
    }
}
