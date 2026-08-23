#nullable enable

#if MODULE_IMGUI
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
#if MODULE_UNITYWEBREQUEST
using UnityEngine.Networking;
#endif

namespace Conduit
{
    sealed class ConduitManualSetupWindow : EditorWindow
    {
        // release documentation tracks current client schemas independently of the installed Unity package
        const string ReadmeUrl = "https://raw.githubusercontent.com/apkd/Conduit/release/README.md";
        const string ReadmePageUrl = "https://github.com/apkd/Conduit/blob/release/README.md";
        const float ContentMargin = 24f;
        const float MinimumWidth = 640f;
        const float MinimumHeight = 640f;

        static Font? monospaceFont;

        string editorName = string.Empty;
        string readmeSectionName = string.Empty;
        string[] readmeLines = Array.Empty<string>();
        string[] httpLines = Array.Empty<string>();
        string[] approvalLines = Array.Empty<string>();
        string loadError = string.Empty;
        bool loading;
        int loadVersion;
        Vector2 scrollPosition;
        GUIStyle? paragraphStyle;
        GUIStyle? codeStyle;
        GUIStyle? stepStyle;
        GUIStyle? titleStyle;

        internal static void Open(ConduitSetupWizardUtility.EditorSpec spec)
        {
            var window = CreateInstance<ConduitManualSetupWindow>();
            window.titleContent = new GUIContent("Conduit manual setup");
#if !UNITY_EDITOR_LINUX
            // xwayland can apply the compositor scale twice to native minimum-size hints
            window.minSize = new(MinimumWidth, MinimumHeight);
#endif
            window.editorName = spec.DisplayName;
            window.readmeSectionName = spec.ManualSetupSection;

            window.ShowUtility();

            // the native window must exist before Unity can translate editor coordinates to its display
            Rect editorBounds = EditorGUIUtility.GetMainWindowPosition();
            var windowSize = new Vector2(MinimumWidth, MinimumHeight);
            window.position = new(editorBounds.center - windowSize * 0.5f, windowSize);

            window.LoadReadmeSectionAsync();
        }

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

        void OnGUI()
        {
            EnsureStyles();
            GUILayout.BeginArea(
                new Rect(
                    ContentMargin,
                    ContentMargin,
                    position.width - ContentMargin * 2f,
                    position.height - ContentMargin * 2f
                )
            );
            EditorGUILayout.LabelField($"Conduit installation steps for {editorName}", titleStyle!);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawInstallStep();
            DrawReadmeStep();
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        void DrawInstallStep()
        {
            DrawStepHeading("1. Download and place the MCP server");
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    DrawParagraphWithLink(
                        "Open the Conduit releases page",
                        " and download `conduit-win-x64.exe`. " +
                        "Move it out of Downloads into a stable location such as `$HOME\\Conduit\\conduit.exe`.",
                        ConduitPackageUpdater.ReleasesUrl
                    );
                    DrawCodeBlock(
                        "New-Item -ItemType Directory -Force \"$HOME\\Conduit\"\n" +
                        "Move-Item \"$HOME\\Downloads\\conduit-win-x64.exe\" \"$HOME\\Conduit\\conduit.exe\""
                    );
                    DrawParagraph(
                        "If Windows blocks the downloaded executable, open its Properties dialog and enable `Unblock`."
                    );
                    break;

                case RuntimePlatform.LinuxEditor:
                    string assetName = ConduitSetupWizardUtility.GetLinuxDownloadAssetName();
                    DrawParagraphWithLink(
                        "Open the Conduit releases page",
                        $" and download `{assetName}`. Place it at `~/.local/bin/conduit` " +
                        "and add the executable permission.",
                        ConduitPackageUpdater.ReleasesUrl
                    );
                    DrawCodeBlock(
                        "mkdir -p \"$HOME/.local/bin\"\n" +
                        $"mv \"$HOME/Downloads/{assetName}\" \"$HOME/.local/bin/conduit\"\n" +
                        "chmod +x \"$HOME/.local/bin/conduit\""
                    );
                    break;

                case RuntimePlatform.OSXEditor:
                    EditorGUILayout.HelpBox(
                        "Conduit does not currently publish a macOS MCP server binary. " +
                        "Run the server on Windows or Linux and connect over HTTP using the README instructions below.",
                        MessageType.Warning
                    );
                    DrawParagraphWithLink(
                        "Open the Conduit releases page",
                        " to see the currently published server builds.",
                        ConduitPackageUpdater.ReleasesUrl
                    );
                    break;

                default:
                    EditorGUILayout.HelpBox(
                        "Conduit does not publish an MCP server binary for this editor platform. " +
                        "Run the server on Windows or Linux and connect over HTTP using the README instructions below.",
                        MessageType.Warning
                    );
                    DrawParagraphWithLink(
                        "Open the Conduit releases page",
                        " to see the currently published server builds.",
                        ConduitPackageUpdater.ReleasesUrl
                    );
                    break;
            }
        }

        void DrawReadmeStep()
        {
            DrawStepHeading($"2. Configure {editorName}");
            if (loading)
            {
                EditorGUILayout.HelpBox("Downloading the latest release README from GitHub...", MessageType.Info);
                return;
            }

            if (loadError.Length > 0)
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
                if (EditorGUILayout.LinkButton("Open the Conduit README on GitHub"))
                    Application.OpenURL(ReadmePageUrl);
                return;
            }

            DrawMarkdown(readmeLines);

            DrawReadmeHeading("http (optional/advanced)");
            DrawNote(
                "Conduit is designed primarily for `stdio`, where each agent gets its own lightweight " +
                "MCP server instance and the client owns its lifetime. Streamable HTTP can serve remote " +
                "clients or let multiple clients share one server, but it adds a persistent service, " +
                "shared failures, and network security concerns. The MCP client does not automatically " +
                "start Conduit in HTTP mode. You should configure your system to run `conduit --http` " +
                "as a daemon or service (or remember to launch it manually)."
            );
            if (httpLines.Length > 0)
                DrawMarkdown(httpLines);
            else
            {
                DrawParagraph(
                    "Start Conduit with `--http` and configure the MCP client to use `http://127.0.0.1:5080`. " +
                    "This README section is not available for the selected client, so check whether it " +
                    "supports streamable HTTP."
                );
                DrawCodeBlock(
                    PatchExecutablePaths(
                        "conduit --http --port 5080 --url http://127.0.0.1:5080",
                        Application.platform,
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    )
                );
            }

            DrawReadmeHeading("approve tool calls");
            if (approvalLines.Length > 0)
                DrawMarkdown(approvalLines);
            else
                DrawParagraph(
                    "Tool approvals are controlled by the MCP client. Use its trust or approval settings " +
                    "to allow Conduit tools you are comfortable running. " +
                    "Codex users can return to Preferences → Conduit and use `Configure tool permissions`."
                );

            EditorGUILayout.Space(12f);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawParagraph($"All done! You can start using `unity` tools in {editorName}.");
            EditorGUILayout.EndVertical();
        }

        async void LoadReadmeSectionAsync()
        {
            // ignore a prior response if this window starts a newer README request
            int currentLoad = ++loadVersion;
            loading = true;
            loadError = string.Empty;
            readmeLines = Array.Empty<string>();
            httpLines = Array.Empty<string>();
            approvalLines = Array.Empty<string>();
            Repaint();

            try
            {
                string markdown = await DownloadReadmeAsync();
                if (currentLoad != loadVersion)
                    return;

                if (ExtractSection(markdown, readmeSectionName) is not { } section)
                    throw new InvalidOperationException(
                        $"The release README does not contain manual setup instructions for {editorName}."
                    );

                string mainSection = section;
                string approvalSection = ExtractHeadingSection(ref mainSection, "approve tool calls");
                string httpSection = ExtractHeadingSection(ref mainSection, "http");
                mainSection = SelectPlatformInstructions(mainSection, Application.platform);
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                mainSection = PatchExecutablePaths(
                    mainSection,
                    Application.platform,
                    homeDirectory
                );
                httpSection = PatchExecutablePaths(
                    httpSection,
                    Application.platform,
                    homeDirectory
                );
                readmeLines = SplitLines(mainSection);
                httpLines = SplitLines(httpSection);
                approvalLines = SplitLines(approvalSection);
            }
            catch (Exception exception)
            {
                if (currentLoad == loadVersion)
                    loadError = $"Could not load the Conduit README from GitHub: {exception.Message}";
            }
            finally
            {
                if (currentLoad == loadVersion)
                {
                    loading = false;
                    Repaint();
                }
            }

            static string[] SplitLines(string value)
                => value.Length == 0
                    ? Array.Empty<string>()
                    : value.Replace("\r\n", "\n").Split('\n');
        }

#if MODULE_UNITYWEBREQUEST
        static async Task<string> DownloadReadmeAsync()
        {
            using var request = UnityWebRequest.Get(ReadmeUrl);
            request.timeout = 10;
            request.SetRequestHeader("User-Agent", "Conduit-Unity-Settings");
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Task.Delay(100);

            if (request.result != UnityWebRequest.Result.Success)
                throw new InvalidOperationException(
                    request.error ?? $"GitHub returned HTTP {request.responseCode}."
                );

            return request.downloadHandler.text;
        }
#else
        static Task<string> DownloadReadmeAsync()
            => Task.FromException<string>(
                new NotSupportedException("The Unity Web Request module is unavailable.")
            );
#endif

        void DrawMarkdown(string[] markdownLines)
        {
            // client sections use a small stable subset; keeping the renderer local avoids a package dependency
            using var pooledCodeLines = ConduitUtility.GetPooledList<string>(out var codeLines);
            bool inCodeBlock = false;
            foreach (string rawLine in markdownLines)
            {
                string line = rawLine.TrimEnd();
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (inCodeBlock)
                        DrawCodeBlock(string.Join("\n", codeLines));

                    codeLines.Clear();
                    inCodeBlock = !inCodeBlock;
                    continue;
                }

                if (inCodeBlock)
                {
                    codeLines.Add(line);
                    continue;
                }

                string trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    EditorGUILayout.Space(5f);
                    continue;
                }

                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    EditorGUILayout.LabelField(GetDisplayHeading(trimmed.TrimStart('#', ' ')), EditorStyles.boldLabel);
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                    trimmed = "• " + trimmed[2..];

                DrawParagraph(trimmed);
            }

            if (codeLines.Count > 0)
                DrawCodeBlock(string.Join("\n", codeLines));
        }

        void DrawStepHeading(string text)
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField(text, stepStyle!);
            EditorGUILayout.Space(3f);
        }

        static void DrawReadmeHeading(string text)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
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

        void DrawNote(string text)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawParagraph(text);
            EditorGUILayout.EndVertical();
        }

        void DrawParagraph(string text)
            => EditorGUILayout.LabelField(FormatInlineCode(text), paragraphStyle!);

        void DrawParagraphWithLink(string linkText, string suffix, string url)
        {
            EditorGUILayout.BeginHorizontal();
            if (EditorGUILayout.LinkButton(linkText, GUILayout.ExpandWidth(false)))
                Application.OpenURL(url);
            EditorGUILayout.LabelField(FormatInlineCode(suffix), paragraphStyle!);
            EditorGUILayout.EndHorizontal();
        }

        void DrawCodeBlock(string code)
        {
            float width = Math.Max(200f, EditorGUIUtility.currentViewWidth - 50f);
            float height = codeStyle!.CalcHeight(new GUIContent(code), width) + 8f;
            EditorGUILayout.SelectableLabel(code, codeStyle, GUILayout.Height(height));
            EditorGUILayout.Space(4f);
        }

        void EnsureStyles()
        {
            paragraphStyle ??= new(EditorStyles.wordWrappedLabel)
            {
                richText = true,
            };
            stepStyle ??= new(EditorStyles.boldLabel)
            {
                fontSize = EditorStyles.boldLabel.fontSize + 1,
            };
            titleStyle ??= new(EditorStyles.boldLabel)
            {
                fontSize = EditorStyles.boldLabel.fontSize + 4,
            };
            codeStyle ??= new(EditorStyles.textArea)
            {
                font = GetMonospaceFont(),
                fontSize = 12,
                wordWrap = true,
            };
        }

        static Font? GetMonospaceFont()
            => monospaceFont ??=
                EditorGUIUtility.Load("Fonts/RobotoMono/RobotoMono-Regular.ttf") as Font
                ?? EditorStyles.textArea.font;

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
