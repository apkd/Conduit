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
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86Path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var vscodeExtensionsPath = Combine(homePath, ".vscode", "extensions");

            if (FindOnPath("codex", "codex.cmd", "codex.exe") != null
                || HasExtension(vscodeExtensionsPath, "openai.chatgpt*")
                || HasExtension(vscodeExtensionsPath, "openai.codex*"))
                return "codex";

            if (FindOnPath("cursor", "cursor.cmd", "cursor.exe") != null
                || File.Exists(Combine(localAppDataPath, "Programs", "Cursor", "Cursor.exe"))
                || File.Exists(@"C:\Program Files\Cursor\Cursor.exe"))
                return "cursor";

            if (FindOnPath("opencode", "opencode.cmd", "opencode.exe") != null
                || File.Exists(Combine(appDataPath, "npm", "opencode.cmd"))
                || HasExtension(vscodeExtensionsPath, "sst-dev.opencode*")
                || HasExtension(vscodeExtensionsPath, "sst-dev.opencode-v2*"))
                return "open-code";

            if (FindOnPath("claude", "claude.cmd", "claude.exe") != null
                || HasExtension(vscodeExtensionsPath, "anthropic.claude-code*"))
                return "claude-code";

            if (FindOnPath("gemini", "gemini.cmd", "gemini.exe") != null
                || File.Exists(Combine(appDataPath, "npm", "gemini.cmd")))
                return "gemini";

            if (FindOnPath("agy", "agy.cmd", "agy.exe") != null
                || HasStartMenuShortcut("Antigravity")
                || File.Exists(Combine(localAppDataPath, "Programs", "Antigravity", "Antigravity.exe")))
                return "antigravity";

            if (FindOnPath("rider64", "rider64.exe", "rider") != null
                || File.Exists(@"C:\Program Files\JetBrains\JetBrains Rider\bin\Rider64.exe")
                || File.Exists(Combine(localAppDataPath, "Programs", "JetBrains Rider", "bin", "Rider64.exe")))
                return "rider-junie";

            if (FindOnPath("cline", "cline.cmd", "cline.exe") != null
                || File.Exists(Combine(appDataPath, "npm", "cline.cmd"))
                || HasExtension(vscodeExtensionsPath, "saoudrizwan.claude-dev*"))
                return "cline";

            if (HasStartMenuShortcut("Claude")
                || File.Exists(Combine(localAppDataPath, "Programs", "Claude", "Claude.exe"))
                || File.Exists(@"C:\Program Files\Claude\Claude.exe"))
                return "claude-desktop";

            if (FindOnPath("copilot", "copilot.cmd", "copilot.exe") != null
                || File.Exists(Combine(appDataPath, "npm", "copilot.cmd")))
                return "github-copilot-cli";

            if (HasExtension(vscodeExtensionsPath, "kilocode.Kilo-Code*"))
                return "kilo-code";

            if (FindOnPath("code", "code.cmd", "code.exe") != null
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

        static string? FindOnPath(params string[] names)
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            for (var directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
            {
                var directory = directories[directoryIndex].Trim();
                for (var nameIndex = 0; nameIndex < names.Length; nameIndex++)
                {
                    var fullPath = Path.Combine(directory, names[nameIndex]);
                    if (File.Exists(fullPath))
                        return fullPath;
                }
            }

            return null;
        }

        static bool HasStartMenuShortcut(string containsName)
        {
            var roots = new[]
            {
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
                Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
            };

            for (var index = 0; index < roots.Length; index++)
            {
                var root = roots[index];
                if (!Directory.Exists(root))
                    continue;

                try
                {
                    if (Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                        .Any(file => Path.GetFileNameWithoutExtension(file).Contains(containsName, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { }
            }

            return false;
        }

        static bool HasExtension(string extensionsPath, string searchPattern)
        {
            if (!Directory.Exists(extensionsPath))
                return false;

            try
            {
                return Directory.EnumerateDirectories(extensionsPath, searchPattern).Any();
            }
            catch
            {
                return false;
            }
        }
    }
}
