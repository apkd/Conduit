#nullable enable

#if MODULE_IMGUI
using System;
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

        internal static void Open(EditorClientSpec spec)
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
                    string assetName = ServerInstallation.GetLinuxDownloadAssetName();
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
                    ConduitManualSetupInstructions.PatchExecutablePaths(
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

                if (ConduitManualSetupInstructions.ExtractSection(markdown, readmeSectionName) is not { } section)
                    throw new InvalidOperationException(
                        $"The release README does not contain manual setup instructions for {editorName}."
                    );

                string mainSection = section;
                string approvalSection = ConduitManualSetupInstructions.ExtractHeadingSection(ref mainSection, "approve tool calls");
                string httpSection = ConduitManualSetupInstructions.ExtractHeadingSection(ref mainSection, "http");
                mainSection = ConduitManualSetupInstructions.SelectPlatformInstructions(mainSection, Application.platform);
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                mainSection = ConduitManualSetupInstructions.PatchExecutablePaths(
                    mainSection,
                    Application.platform,
                    homeDirectory
                );
                httpSection = ConduitManualSetupInstructions.PatchExecutablePaths(
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
            using var pooledCodeLines = ConduitPool.GetPooledList<string>(out var codeLines);
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
                    EditorGUILayout.LabelField(ConduitManualSetupInstructions.GetDisplayHeading(trimmed.TrimStart('#', ' ')), EditorStyles.boldLabel);
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

        void DrawNote(string text)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            DrawParagraph(text);
            EditorGUILayout.EndVertical();
        }

        void DrawParagraph(string text)
            => EditorGUILayout.LabelField(ConduitManualSetupInstructions.FormatInlineCode(text), paragraphStyle!);

        void DrawParagraphWithLink(string linkText, string suffix, string url)
        {
            EditorGUILayout.BeginHorizontal();
            if (EditorGUILayout.LinkButton(linkText, GUILayout.ExpandWidth(false)))
                Application.OpenURL(url);
            EditorGUILayout.LabelField(ConduitManualSetupInstructions.FormatInlineCode(suffix), paragraphStyle!);
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
    }
}
#endif
