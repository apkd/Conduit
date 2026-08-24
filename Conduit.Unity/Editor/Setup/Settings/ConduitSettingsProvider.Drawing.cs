#nullable enable

#if MODULE_IMGUI
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Conduit
{
    sealed partial class ConduitSettingsProvider : SettingsProvider
    {
        static void DrawOutdatedWarning(
            bool serverOutdated,
            bool clientOutdated,
            bool isNixOsManaged
        )
        {
            if (!serverOutdated && !clientOutdated)
                return;

            string message = (isNixOsManaged, serverOutdated, clientOutdated) switch
            {
                (true, _, _) =>
                    "A new version of Conduit is available. " +
                    "Update the Conduit NixOS package through your NixOS configuration.",
                (_, true, true) =>
                    "New versions of the Conduit MCP server and Unity MCP client are available. " +
                    "Use the wizard below to update, or download the latest release from:",
                (_, true, _) =>
                    "A new version of the Conduit MCP server is available. " +
                    "Use the wizard below to update, or download the latest release from:",
                _ =>
                    "A new version of the Conduit Unity MCP client is available. " +
                    "Use the wizard below to update, or download the latest release from:",
            };

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(
                EditorGUIUtility.IconContent("console.warnicon"),
                GUILayout.Width(32f),
                GUILayout.Height(32f)
            );
            EditorGUILayout.BeginVertical();
            EditorGUILayout.LabelField(message, EditorStyles.wordWrappedLabel);
            if (!isNixOsManaged && EditorGUILayout.LinkButton(ConduitPackageUpdater.ReleasesUrl))
                Application.OpenURL(ConduitPackageUpdater.ReleasesUrl);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
        }

        static void DrawOtherSettings(ConduitSettings settings)
        {
            EditorGUI.BeginChangeCheck();
            bool unfocusedGameView = EditorGUILayout.ToggleLeft(
                "Hide game view in play mode",
                settings.UnfocusedGameView
            );
            if (EditorGUI.EndChangeCheck())
                settings.SetUnfocusedGameView(unfocusedGameView);

            EditorGUILayout.HelpBox(UnfocusedGameViewDescription, MessageType.None);

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            bool lowResolutionPlayMode = EditorGUILayout.ToggleLeft(
                "Low resolution play mode",
                settings.LowResolutionPlayMode
            );
            if (EditorGUI.EndChangeCheck())
                settings.SetLowResolutionPlayMode(lowResolutionPlayMode);

            EditorGUILayout.HelpBox(LowResolutionPlayModeDescription, MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool trackToolUsage = EditorGUILayout.ToggleLeft(
                "Local tool usage tracking",
                ConduitToolUsage.Enabled,
                GUILayout.ExpandWidth(false)
            );
            if (EditorGUI.EndChangeCheck())
                ConduitToolUsage.Enabled = trackToolUsage;
            GUILayout.Space(6f);
            if (EditorGUILayout.LinkButton("Show data"))
                ConduitToolUsageWindow.Open();
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(LocalToolUsageDescription, MessageType.None);

            EditorGUILayout.Space();

            // player defines follow the active compilation target; the server target has a separate
            // define set even though Unity maps it to the standalone build target group
#if UNITY_SERVER
            var buildTarget = NamedBuildTarget.Server;
#else
            var buildTarget = NamedBuildTarget.FromBuildTargetGroup(
                BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget)
            );
#endif
            PlayerSettings.GetScriptingDefineSymbols(buildTarget, out var defines);
            EditorGUI.BeginChangeCheck();
            bool includeInDebugBuilds = EditorGUILayout.ToggleLeft(
                "Enable Conduit in development player builds",
                Array.IndexOf(defines, DebugBuildDefine) >= 0
            );
            EditorGUILayout.HelpBox(DebugBuildDescription, MessageType.None);

            if (EditorGUI.EndChangeCheck())
            {
                var updatedDefines = new List<string>(defines);
                if (includeInDebugBuilds)
                    updatedDefines.Add(DebugBuildDefine);
                else
                    updatedDefines.RemoveAll(static define => define == DebugBuildDefine);

                PlayerSettings.SetScriptingDefineSymbols(buildTarget, updatedDefines.ToArray());
            }

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            bool preserveSnippets = EditorGUILayout.ToggleLeft(
                "Preserve `execute_code` and `detour` snippets",
                ConduitSnippetStorage.PreserveSnippets
            );
            if (EditorGUI.EndChangeCheck())
                ConduitSnippetStorage.PreserveSnippets = preserveSnippets;

            EditorGUILayout.HelpBox(PreserveSnippetsDescription, MessageType.None);
        }

        void DrawSelectedEditor(EditorClientSpec[] specs, ConduitSettings settings)
        {
            EditorClientSpec? selectedSpec = null;
            foreach (var spec in specs)
                if (spec.Id == settings.SelectedEditorId)
                {
                    selectedSpec = spec;
                    break;
                }

            string selectedLabel = selectedSpec is null
                ? "Select code editor..."
                : GetEditorLabel(selectedSpec);
            selectedEditorContent.text = selectedLabel;
            var rect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(),
                codeEditorLabel
            );
            if (!EditorGUI.DropdownButton(
                    rect,
                    selectedEditorContent,
                    FocusType.Keyboard,
                    EditorStyles.popup
                ))
                return;

            var menu = new GenericMenu();
            if (installedEditorIds.Count == 0)
                menu.AddItem(
                    new GUIContent("Select code editor..."),
                    selectedSpec is null,
                    () => SelectEditor(null)
                );

            foreach (var spec in specs)
                if (installedEditorIds.Contains(spec.Id))
                    AddEditor(spec);

            if (installedEditorIds.Count > 0 && installedEditorIds.Count < specs.Length)
                menu.AddSeparator(string.Empty);

            foreach (var spec in specs)
                if (!installedEditorIds.Contains(spec.Id))
                    AddEditor(spec);

            menu.DropDown(rect);

            string GetEditorLabel(EditorClientSpec spec)
                => editorLabels[spec.Id];

            void AddEditor(EditorClientSpec spec)
                => menu.AddItem(
                    new GUIContent(GetEditorLabel(spec)),
                    spec == selectedSpec,
                    () => SelectEditor(spec)
                );

            void SelectEditor(EditorClientSpec? spec)
            {
                settings.SetSelectedEditorId(spec?.Id ?? string.Empty);
                if (spec is not null && EditorConfigurationPaths.SupportsProjectConfiguration(spec))
                    settings.SetConfigurationLocation(
                        EditorConfigurationPaths.GetPreferredConfigurationLocation(
                            spec,
                            SetupConfigurationLocation.Project
                        )
                    );
                actionErrors.Clear();
                SettingsService.RepaintAllSettingsWindow();
            }
        }

        void DrawConfigurationLocation(
            EditorClientSpec[] specs,
            ConduitSettings settings
        )
        {
            if (settings.SelectedEditorId.Length == 0)
                return;

            var spec = ConduitSettingsSelection.GetSelectedSpec(
                specs,
                settings.SelectedEditorId
            );
            if (!EditorConfigurationPaths.SupportsProjectConfiguration(spec))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    configurationLocationLabel,
                    GUILayout.Width(EditorGUIUtility.labelWidth)
                );
                EditorGUILayout.LabelField(
                    "This editor does not support per-project MCP setup.",
                    EditorStyles.wordWrappedLabel
                );
                EditorGUILayout.EndHorizontal();
                return;
            }

            var rect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(),
                configurationLocationLabel
            );
            float buttonWidth = rect.width * 0.5f;
            var projectRect = new Rect(rect.x, rect.y, buttonWidth, rect.height);
            var userRect = new Rect(rect.x + buttonWidth, rect.y, buttonWidth, rect.height);
            var location = settings.ConfigurationLocation;

            if (GUI.Toggle(
                    projectRect,
                    location == SetupConfigurationLocation.Project,
                    projectConfigurationLabel,
                    EditorStyles.miniButtonLeft
                ))
                location = SetupConfigurationLocation.Project;
            if (GUI.Toggle(
                    userRect,
                    location == SetupConfigurationLocation.User,
                    userConfigurationLabel,
                    EditorStyles.miniButtonRight
                ))
                location = SetupConfigurationLocation.User;

            if (location == settings.ConfigurationLocation)
                return;

            settings.SetConfigurationLocation(location);
            actionErrors.Clear();
            SettingsService.RepaintAllSettingsWindow();
        }

        static void DrawPaths(
            EditorClientSpec[] specs,
            ConduitSettings settings,
            string effectiveExecutablePath
        )
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(
                    "Server Executable",
                    effectiveExecutablePath.Length == 0 ? "<not set>" : effectiveExecutablePath
                );

            if (settings.SelectedEditorId.Length == 0)
                return;

            var spec = ConduitSettingsSelection.GetSelectedSpec(
                specs,
                settings.SelectedEditorId
            );
            var location = ConduitSettingsSelection.GetConfigurationLocation(spec, settings);
            string configPath = EditorConfigurationPaths.GetDisplayConfigPath(spec, location)
                                ?? "<unsupported on current OS>";
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField("Config Path", configPath);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Manual setup", GUILayout.Width(EditorGUIUtility.labelWidth));
            if (EditorGUILayout.LinkButton("Click here to open manual setup instructions."))
                ConduitManualSetupWindow.Open(spec);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawButtons(
            EditorClientSpec[] specs,
            ConduitSettings settings,
            string effectiveExecutablePath,
            SetupButtonModel downloadModel,
            bool isNixOsManaged
        )
        {
            var location = ConduitSettingsSelection.GetConfigurationLocation(specs, settings);
            if (!isNixOsManaged && packageUpdateStatus.State is ConduitPackageUpdateState.Outdated)
            {
                // a UPM update reloads this code, so it takes precedence over server actions in the current view
                DrawButton(
                    SetupActionKind.UpdatePackage,
                    EvaluatePackageUpdateButton(),
                    async () =>
                    {
                        await ConduitPackageUpdater.UpdateAsync();
                        packageUpdateStatus = new(ConduitPackageUpdateState.Current);
                    }
                );
            }
            else
            {
                DrawButton(
                    SetupActionKind.DownloadServer,
                    downloadModel,
                    async () =>
                    {
                        string executablePath = await ServerInstaller.DownloadServerAsync(
                            location,
                            effectiveExecutablePath
                        );
                        settings.SetServerExecutablePath(executablePath);
                    }
                );
            }

            if (settings.SelectedEditorId.Length == 0)
                return;

            var spec = ConduitSettingsSelection.GetSelectedSpec(
                specs,
                settings.SelectedEditorId
            );
            DrawButton(
                SetupActionKind.ConfigureEditor,
                SetupActionEvaluator.EvaluateConfigureButton(
                    spec,
                    location,
                    effectiveExecutablePath,
                    runningAction == SetupActionKind.ConfigureEditor,
                    HasError(SetupActionKind.ConfigureEditor)
                ),
                () =>
                {
                    EditorConfiguration.ConfigureEditor(spec, location, effectiveExecutablePath);
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            );

            if (spec.Id != "codex")
                return;

            DrawButton(
                SetupActionKind.ConfigureCodexPermissions,
                SetupActionEvaluator.EvaluateCodexPermissionsButton(
                    location,
                    effectiveExecutablePath,
                    runningAction == SetupActionKind.ConfigureCodexPermissions,
                    HasError(SetupActionKind.ConfigureCodexPermissions)
                ),
                () =>
                {
                    EditorConfiguration.ConfigureCodexPermissions(location);
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            );
        }

        void BeginGroup(string title)
        {
            EnsureGroupStyles();
            EditorGUILayout.BeginVertical(groupBoxStyle!);
            GUILayout.Label(title, groupHeaderStyle!);
            EditorGUILayout.BeginVertical(groupContentStyle!);
        }

        static void EndGroup()
        {
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndVertical();
        }

        void EnsureGroupStyles()
        {
            if (groupBoxStyle is not null)
                return;

            // the toolbar and padded content share one border like an inspector box group
            groupBoxStyle = new(GUI.skin.box)
            {
                margin = new RectOffset(0, 0, 4, 4),
                padding = new RectOffset(1, 1, 1, 1),
            };
            groupHeaderStyle = new(EditorStyles.toolbar)
            {
                alignment = TextAnchor.MiddleLeft,
                fixedHeight = 20f,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(6, 6, 0, 0),
                stretchWidth = true,
            };
            groupContentStyle = new()
            {
                padding = new RectOffset(6, 6, 6, 6),
                stretchWidth = true,
            };
        }

    }
}
#endif
