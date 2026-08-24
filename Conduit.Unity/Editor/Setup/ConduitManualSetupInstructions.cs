#nullable enable

#if MODULE_IMGUI
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Conduit
{
    static class ConduitManualSetupInstructions
    {
        internal static string? ExtractSection(string markdown, string sectionName)
        {
            // root README instructions use flat details blocks around each MCP client
            string marker = $"<summary>{sectionName}</summary>";
            int markerIndex = markdown.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return null;

            int contentStart = markerIndex + marker.Length;
            int contentEnd = markdown.IndexOf("</details>", contentStart, StringComparison.OrdinalIgnoreCase);
            return contentEnd < 0 ? null : markdown[contentStart..contentEnd].Trim();
        }

        internal static string ExtractHeadingSection(ref string markdown, string heading)
        {
            var lines = new List<string>(markdown.Replace("\r\n", "\n").Split('\n'));
            int startIndex = -1;
            for (int index = 0, count = lines.Count; index < count; ++index)
                if (IsHeading(lines[index], heading))
                {
                    startIndex = index;
                    break;
                }

            if (startIndex < 0)
                return string.Empty;

            int endIndex = startIndex + 1;
            while (endIndex < lines.Count && !lines[endIndex].TrimStart().StartsWith("#", StringComparison.Ordinal))
                endIndex++;

            string section = string.Join("\n", lines.GetRange(startIndex + 1, endIndex - startIndex - 1)).Trim();
            lines.RemoveRange(startIndex, endIndex - startIndex);
            markdown = string.Join("\n", lines).Trim();
            return section;

            static bool IsHeading(string line, string expectedHeading)
            {
                string trimmed = line.TrimStart();
                return trimmed.StartsWith("#", StringComparison.Ordinal)
                       && string.Equals(
                           trimmed.TrimStart('#', ' '),
                           expectedHeading,
                           StringComparison.OrdinalIgnoreCase
                       );
            }
        }

        internal static string SelectPlatformInstructions(string markdown, RuntimePlatform platform)
        {
            string platformName = platform switch
            {
                RuntimePlatform.WindowsEditor => "Windows",
                RuntimePlatform.LinuxEditor => "Linux",
                RuntimePlatform.OSXEditor => "macOS",
                _ => string.Empty,
            };
            if (platformName.Length == 0)
                return markdown.Trim();

            var selectedLines = new List<string>();
            bool includeSection = true;
            foreach (string line in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    string heading = trimmed.TrimStart('#', ' ');
                    includeSection = !heading.StartsWith("stdio |", StringComparison.OrdinalIgnoreCase)
                                     || heading.Contains(platformName, StringComparison.OrdinalIgnoreCase);
                }

                if (includeSection)
                    selectedLines.Add(line);
            }

            return string.Join("\n", selectedLines).Trim();
        }

        internal static string PatchExecutablePaths(
            string markdown,
            RuntimePlatform platform,
            string homeDirectory
        )
        {
            if (string.IsNullOrWhiteSpace(homeDirectory))
                return markdown;

            // the release README uses canonical examples so paths can be replaced without parsing every config format
            if (platform == RuntimePlatform.LinuxEditor)
            {
                string executablePath = homeDirectory.TrimEnd('/') + "/.local/bin/conduit";
                markdown = ReplaceAll(
                    markdown,
                    executablePath,
                    "/home/you/src/Conduit/Conduit.Server/publish/linux-x64/conduit",
                    "/home/you/.local/bin/conduit"
                );
                markdown = ReplaceAll(
                    markdown,
                    homeDirectory.TrimEnd('/') + "/.local/bin",
                    "/home/you/src/Conduit",
                    "/home/you/.local/bin"
                );
                return markdown.Replace(
                    "conduit --http",
                    executablePath + " --http",
                    StringComparison.Ordinal
                );
            }

            if (platform != RuntimePlatform.WindowsEditor)
                return markdown;

            string windowsPath = homeDirectory.TrimEnd('\\', '/') + "\\Conduit\\conduit.exe";
            string escapedWindowsPath = windowsPath.Replace("\\", "\\\\");
            markdown = ReplaceAll(
                markdown,
                escapedWindowsPath,
                @"C:\\src\\Conduit\\Conduit.Server\\publish\\win-x64\\conduit.exe",
                @"C:\\Users\\you\\Conduit\\conduit.exe"
            );
            markdown = ReplaceAll(
                markdown,
                windowsPath,
                @"C:\src\Conduit\Conduit.Server\publish\win-x64\conduit.exe",
                @"C:\Users\you\Conduit\conduit.exe"
            );
            string windowsDirectory = windowsPath[..windowsPath.LastIndexOf('\\')];
            markdown = ReplaceAll(
                markdown,
                windowsDirectory.Replace("\\", "\\\\"),
                @"C:\\src\\Conduit",
                @"C:\\Users\\you\\Conduit"
            );
            markdown = ReplaceAll(
                markdown,
                windowsDirectory,
                @"C:\src\Conduit",
                @"C:\Users\you\Conduit"
            );

            if (windowsPath.Length > 2 && windowsPath[1] == ':')
            {
                // windows users may run a WSL client, which needs the same file expressed as a mount path
                string wslPath = $"/mnt/{char.ToLowerInvariant(windowsPath[0])}" +
                                 windowsPath[2..].Replace('\\', '/');
                string wslDirectory = wslPath[..wslPath.LastIndexOf('/')];
                markdown = ReplaceAll(
                    markdown,
                    wslPath,
                    "/mnt/c/src/Conduit/Conduit.Server/publish/win-x64/conduit.exe",
                    "/mnt/c/Users/you/Conduit/conduit.exe"
                );
                markdown = ReplaceAll(
                    markdown,
                    wslDirectory,
                    "/mnt/c/src/Conduit",
                    "/mnt/c/Users/you/Conduit"
                );
            }

            if (windowsPath.Contains(' '))
                markdown = markdown.Replace($"-- {windowsPath}", $"-- \"{windowsPath}\"");
            return markdown.Replace(
                "conduit --http",
                $"& \"{windowsPath}\" --http",
                StringComparison.Ordinal
            );

            static string ReplaceAll(string text, string replacement, params string[] examples)
            {
                foreach (string example in examples)
                    text = text.Replace(example, replacement, StringComparison.Ordinal);
                return text;
            }
        }

        internal static string GetDisplayHeading(string heading)
        {
            // platform-qualified README headings stay machine-readable while the window adds transport guidance
            if (!heading.StartsWith("stdio |", StringComparison.OrdinalIgnoreCase))
                return heading;

            if (heading.EndsWith("(Native)", StringComparison.OrdinalIgnoreCase))
                return "stdio (recommended) | native Windows";
            if (heading.EndsWith("(WSL)", StringComparison.OrdinalIgnoreCase))
                return "stdio (recommended) | WSL";
            return "stdio (recommended)";
        }

        internal static string FormatInlineCode(string text)
        {
            // escape downloaded text while allowing only the rich-text tags introduced by this formatter
            var builder = new StringBuilder(text.Length + 32);
            bool inCode = false;
            bool inBold = false;
            string codeColor = EditorGUIUtility.isProSkin ? "#E6C07B" : "#795E26";
            for (int index = 0, length = text.Length; index < length; ++index)
            {
                if (text[index] == '`')
                {
                    builder.Append(inCode ? "</b></color>" : $"<color={codeColor}><b>");
                    inCode = !inCode;
                    continue;
                }

                if (index + 1 < text.Length && text[index] == '*' && text[index + 1] == '*')
                {
                    builder.Append(inBold ? "</b>" : "<b>");
                    inBold = !inBold;
                    index++;
                    continue;
                }

                builder.Append(text[index] switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    _ => text[index],
                });
            }

            if (inCode)
                builder.Append("</b></color>");
            if (inBold)
                builder.Append("</b>");
            return builder.ToString();
        }
    }
}
#endif
