#nullable enable

#if MODULE_IMGUI
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Conduit
{
    sealed class ConduitSettingsProvider : SettingsProvider
    {
        const string SettingsPath = "Preferences/Conduit";
        const string UnfocusedGameViewDescription =
            "Saves some power by keeping the game view window unfocused during play mode runs.";
        const string LowResolutionPlayModeDescription =
            "Lowers the game view resolution to 480x320 when executing play mode commands. " +
            "This helps preserve GPU resources. Useful when running multiple Unity instances at the same time.";
        const string LocalToolUsageDescription =
            "Count the number of times each MCP tool was used and the average call duration. " +
            "This data is stored locally and never sent anywhere. Useful for analyzing and " +
            "improving your MCP workflows.";
        const string DebugBuildDefine = "CONDUIT_INCLUDE_IN_DEBUG_BUILDS";
        const string DebugBuildDescription =
            "Enables CONDUIT_INCLUDE_IN_DEBUG_BUILDS in this project, which lets the Conduit MCP " +
            "communicate with the built development player, supporting a subset of the editor tools.";
        const string PreserveSnippetsDescription =
            "Saves code snippets in Library/Conduit instead of Temp/Conduit. This extends their " +
            "lifetime, allowing agents to call them across multiple editor sessions. Unity " +
            "automatically deletes files in the Temp directory during editor startup.";
        static readonly Color successColor = new(0.45f, 0.8f, 0.45f);
        static readonly Color errorColor = new(0.85f, 0.45f, 0.45f);
        static readonly Color enabledColor = new(0.8f, 0.8f, 0.8f);
        static readonly GUIContent configurationLocationLabel = new(
            "Configuration",
            "Choose the installation scope for the selected code editor. The scope controls where " +
            "its MCP configuration is written and where wizard-managed server binaries are installed.\n\n" +
            "Project scope isolates Conduit to this project. User scope shares one setup across projects."
        );
        static readonly GUIContent projectConfigurationLabel = new(
            "In project folder",
            "Configure Conduit only for the currently open Unity project.\n\n" +
            "The MCP configuration is written inside the project folder. When the wizard installs the " +
            "server, it places both the Windows and Linux binaries in the project's Conduit directory.\n\n" +
            "Use this for an isolated setup that leaves your user profile and other projects untouched. " +
            "Each project must be configured separately, and the downloaded binaries occupy space inside it."
        );
        static readonly GUIContent userConfigurationLabel = new(
            "In user profile",
            "Configure Conduit for this user account across projects.\n\n" +
            "The MCP configuration is written to the selected editor's user profile. When the wizard " +
            "installs the server, it downloads only the binary for the current operating system to the " +
            "standard user location.\n\n" +
            "Use this for one reusable setup. It changes your personal editor configuration, does not " +
            "travel with the project, and makes Conduit available to other projects using that profile."
        );

        readonly Dictionary<ConduitSetupWizardUtility.ActionKind, bool> actionErrors = new();
        readonly HashSet<string> installedEditorIds = new(StringComparer.Ordinal);
        ConduitSetupWizardUtility.ActionKind? runningAction;
        ConduitPackageUpdateStatus packageUpdateStatus;
        GUIStyle? groupBoxStyle;
        GUIStyle? groupHeaderStyle;
        GUIStyle? groupContentStyle;
        GUIStyle? hintStyle;
        bool active;
        // reused Preferences providers need an activation generation to reject stale async results
        int activationVersion;

        ConduitSettingsProvider()
            : base(
                SettingsPath,
                SettingsScope.User,
                new[]
                {
                    "Conduit",
                    "MCP",
                    "Game View",
                    "Resolution",
                    "GPU",
                    "Play Mode",
                    "Tests",
                    "Usage",
                    "Tracking",
                    "Server",
                    "Editor",
                    "Configuration",
                    "Project",
                    "Profile",
                    "Development",
                    "Player",
                    "Builds",
                    DebugBuildDefine,
                    "Snippets",
                    "Preserve",
                    "Library",
                    "Temp",
                    "execute_code",
                    "detour",
                }
            )
            => label = "Conduit";

        [SettingsProvider]
        static SettingsProvider Create()
            => new ConduitSettingsProvider();

        void Activate()
        {
            if (active)
                return;

            active = true;
            var settings = ConduitSettings.instance;
            var specs = ConduitSetupWizardUtility.GetEditorSpecs();
            RefreshInstalledEditors(specs);
            if (settings.SelectedEditorId.Length == 0 && installedEditorIds.Count > 0)
                foreach (var spec in specs)
                    if (installedEditorIds.Contains(spec.Id))
                    {
                        settings.SetSelectedEditorId(spec.Id);
                        break;
                    }

            if (settings.SelectedEditorId.Length > 0)
            {
                var spec = ConduitSetupWizardUtility.FindEditorSpec(settings.SelectedEditorId);
                if (ConduitSetupWizardUtility.SupportsProjectConfiguration(spec))
                    settings.SetConfigurationLocation(
                        ConduitSetupWizardUtility.GetPreferredConfigurationLocation(
                            spec,
                            settings.ConfigurationLocation
                        )
                    );
            }

            int currentActivation = ++activationVersion;
            packageUpdateStatus = default;
            CheckPackageUpdateAsync(currentActivation);
        }

        public override void OnDeactivate()
        {
            active = false;
            activationVersion++;
        }

        public override void OnGUI(string searchContext)
        {
            Activate();
            var settings = ConduitSettings.instance;
            var specs = ConduitSetupWizardUtility.GetEditorSpecs();
            var location = GetConfigurationLocation(specs, settings);
            string configuredExecutablePath = GetConfiguredExecutablePath(specs, settings);
            string effectiveExecutablePath = ConduitSetupWizardUtility.GetEffectiveExecutablePath(
                location,
                settings.ServerExecutablePath,
                configuredExecutablePath
            );
            // updating through UPM would bypass the package owner and change only the client.
            bool isNixOsManaged = ConduitSetupWizardUtility.IsNixOsManagedExecutablePath(
                effectiveExecutablePath
            );
            var downloadModel = ConduitSetupWizardUtility.EvaluateDownloadButton(
                location,
                settings.ServerExecutablePath,
                configuredExecutablePath,
                runningAction == ConduitSetupWizardUtility.ActionKind.DownloadServer,
                HasError(ConduitSetupWizardUtility.ActionKind.DownloadServer)
            );

            bool clientOutdated = packageUpdateStatus.State is ConduitPackageUpdateState.Outdated;
            DrawOutdatedWarning(
                downloadModel.IsOutdated,
                clientOutdated,
                isNixOsManaged
            );

            BeginGroup("Installation Options");
            DrawSelectedEditor(specs, settings);
            DrawConfigurationLocation(specs, settings);
            DrawPaths(specs, settings, effectiveExecutablePath);
            EndGroup();

            BeginGroup("Setup Wizard");
            DrawButtons(
                specs,
                settings,
                effectiveExecutablePath,
                downloadModel,
                isNixOsManaged
            );
            EndGroup();

            BeginGroup("Other Settings");
            DrawOtherSettings(settings);
            EndGroup();
        }

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

        void DrawSelectedEditor(ConduitSetupWizardUtility.EditorSpec[] specs, ConduitSettings settings)
        {
            ConduitSetupWizardUtility.EditorSpec? selectedSpec = null;
            foreach (var spec in specs)
                if (spec.Id == settings.SelectedEditorId)
                {
                    selectedSpec = spec;
                    break;
                }

            string selectedLabel = selectedSpec is null
                ? "Select code editor..."
                : GetEditorLabel(selectedSpec);
            var rect = EditorGUI.PrefixLabel(
                EditorGUILayout.GetControlRect(),
                new GUIContent("Code Editor")
            );
            if (!EditorGUI.DropdownButton(
                    rect,
                    new GUIContent(selectedLabel),
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

            string GetEditorLabel(ConduitSetupWizardUtility.EditorSpec spec)
                => installedEditorIds.Contains(spec.Id)
                    ? $"{spec.DisplayName} (installed)"
                    : spec.DisplayName;

            void AddEditor(ConduitSetupWizardUtility.EditorSpec spec)
                => menu.AddItem(
                    new GUIContent(GetEditorLabel(spec)),
                    spec == selectedSpec,
                    () => SelectEditor(spec)
                );

            void SelectEditor(ConduitSetupWizardUtility.EditorSpec? spec)
            {
                settings.SetSelectedEditorId(spec?.Id ?? string.Empty);
                if (spec is not null && ConduitSetupWizardUtility.SupportsProjectConfiguration(spec))
                    settings.SetConfigurationLocation(
                        ConduitSetupWizardUtility.GetPreferredConfigurationLocation(
                            spec,
                            ConduitSetupWizardUtility.ConfigurationLocation.Project
                        )
                    );
                actionErrors.Clear();
                SettingsService.RepaintAllSettingsWindow();
            }
        }

        void DrawConfigurationLocation(
            ConduitSetupWizardUtility.EditorSpec[] specs,
            ConduitSettings settings
        )
        {
            if (settings.SelectedEditorId.Length == 0)
                return;

            var spec = GetSelectedSpec(specs, settings.SelectedEditorId);
            if (!ConduitSetupWizardUtility.SupportsProjectConfiguration(spec))
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
                    location == ConduitSetupWizardUtility.ConfigurationLocation.Project,
                    projectConfigurationLabel,
                    EditorStyles.miniButtonLeft
                ))
                location = ConduitSetupWizardUtility.ConfigurationLocation.Project;
            if (GUI.Toggle(
                    userRect,
                    location == ConduitSetupWizardUtility.ConfigurationLocation.User,
                    userConfigurationLabel,
                    EditorStyles.miniButtonRight
                ))
                location = ConduitSetupWizardUtility.ConfigurationLocation.User;

            if (location == settings.ConfigurationLocation)
                return;

            settings.SetConfigurationLocation(location);
            actionErrors.Clear();
            SettingsService.RepaintAllSettingsWindow();
        }

        static void DrawPaths(
            ConduitSetupWizardUtility.EditorSpec[] specs,
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

            var spec = GetSelectedSpec(specs, settings.SelectedEditorId);
            var location = GetConfigurationLocation(spec, settings);
            string configPath = ConduitSetupWizardUtility.GetDisplayConfigPath(spec, location)
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
            ConduitSetupWizardUtility.EditorSpec[] specs,
            ConduitSettings settings,
            string effectiveExecutablePath,
            ConduitSetupWizardUtility.ButtonModel downloadModel,
            bool isNixOsManaged
        )
        {
            var location = GetConfigurationLocation(specs, settings);
            if (!isNixOsManaged && packageUpdateStatus.State is ConduitPackageUpdateState.Outdated)
            {
                // a UPM update reloads this code, so it takes precedence over server actions in the current view
                DrawButton(
                    ConduitSetupWizardUtility.ActionKind.UpdatePackage,
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
                    ConduitSetupWizardUtility.ActionKind.DownloadServer,
                    downloadModel,
                    async () =>
                    {
                        string executablePath = await ConduitSetupWizardUtility.DownloadServerAsync(
                            location,
                            effectiveExecutablePath
                        );
                        settings.SetServerExecutablePath(executablePath);
                    }
                );
            }

            if (settings.SelectedEditorId.Length == 0)
                return;

            var spec = GetSelectedSpec(specs, settings.SelectedEditorId);
            DrawButton(
                ConduitSetupWizardUtility.ActionKind.ConfigureEditor,
                ConduitSetupWizardUtility.EvaluateConfigureButton(
                    spec,
                    location,
                    effectiveExecutablePath,
                    runningAction == ConduitSetupWizardUtility.ActionKind.ConfigureEditor,
                    HasError(ConduitSetupWizardUtility.ActionKind.ConfigureEditor)
                ),
                () =>
                {
                    ConduitSetupWizardUtility.ConfigureEditor(spec, location, effectiveExecutablePath);
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            );

            if (spec.Id != "codex")
                return;

            DrawButton(
                ConduitSetupWizardUtility.ActionKind.ConfigureCodexPermissions,
                ConduitSetupWizardUtility.EvaluateCodexPermissionsButton(
                    location,
                    effectiveExecutablePath,
                    runningAction == ConduitSetupWizardUtility.ActionKind.ConfigureCodexPermissions,
                    HasError(ConduitSetupWizardUtility.ActionKind.ConfigureCodexPermissions)
                ),
                () =>
                {
                    ConduitSetupWizardUtility.ConfigureCodexPermissions(location);
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            );
        }

        ConduitSetupWizardUtility.ButtonModel EvaluatePackageUpdateButton()
        {
            if (runningAction == ConduitSetupWizardUtility.ActionKind.UpdatePackage)
                return new()
                {
                    State = ConduitSetupWizardUtility.ActionState.Running,
                    Label = "Updating the Unity package...",
                    Hint = "Unity Package Manager is updating Conduit from the release branch.",
                    IsOutdated = true,
                };

            if (HasError(ConduitSetupWizardUtility.ActionKind.UpdatePackage))
                return new()
                {
                    State = ConduitSetupWizardUtility.ActionState.Error,
                    Label = "Update the Unity package",
                    Hint = "Unity Package Manager could not update Conduit. Check the Console for the full error.",
                    IsOutdated = true,
                };

            return new()
            {
                State = ConduitSetupWizardUtility.ActionState.Enabled,
                Label = "Update the Unity package",
                Hint =
                    $"The installed package commit {ShortHash(packageUpdateStatus.InstalledHash)} is older than " +
                    $"the release commit {ShortHash(packageUpdateStatus.LatestHash)}. " +
                    "Update it through Unity Package Manager.",
                IsOutdated = true,
            };
        }

        void DrawButton(
            ConduitSetupWizardUtility.ActionKind actionKind,
            ConduitSetupWizardUtility.ButtonModel model,
            Func<System.Threading.Tasks.Task> callback
        )
        {
            EditorGUILayout.BeginVertical(GUI.skin.box);
            using (new EditorGUI.DisabledScope(model.State is not ConduitSetupWizardUtility.ActionState.Enabled))
            {
                var previousColor = GUI.backgroundColor;
                GUI.backgroundColor = model.State switch
                {
                    ConduitSetupWizardUtility.ActionState.Success => successColor,
                    ConduitSetupWizardUtility.ActionState.Error => errorColor,
                    ConduitSetupWizardUtility.ActionState.Enabled => enabledColor,
                    _ => GUI.backgroundColor,
                };
                if (GUILayout.Button(model.Label, GUILayout.Height(28f)))
                    RunAction(actionKind, callback);
                GUI.backgroundColor = previousColor;
            }

            if (model.Hint.Length > 0)
            {
                hintStyle ??= new(EditorStyles.wordWrappedMiniLabel) { richText = true };
                EditorGUILayout.LabelField(
                    ConduitManualSetupWindow.FormatInlineCode(model.Hint),
                    hintStyle
                );
            }

            EditorGUILayout.EndVertical();
        }

        async void RunAction(
            ConduitSetupWizardUtility.ActionKind actionKind,
            Func<System.Threading.Tasks.Task> callback
        )
        {
            if (runningAction is not null)
                return;

            runningAction = actionKind;
            actionErrors.Remove(actionKind);
            SettingsService.RepaintAllSettingsWindow();

            try
            {
                await callback();
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                actionErrors[actionKind] = true;
                Debug.LogException(exception);
            }
            finally
            {
                runningAction = null;
                RefreshInstalledEditors(ConduitSetupWizardUtility.GetEditorSpecs());
                SettingsService.RepaintAllSettingsWindow();
            }
        }

        async void CheckPackageUpdateAsync(int currentActivation)
        {
            var status = await ConduitPackageUpdater.CheckAsync();
            if (currentActivation != activationVersion)
                return;

            packageUpdateStatus = status;
            SettingsService.RepaintAllSettingsWindow();
        }

        bool HasError(ConduitSetupWizardUtility.ActionKind actionKind)
            => actionErrors.TryGetValue(actionKind, out var hasError) && hasError;

        void RefreshInstalledEditors(ConduitSetupWizardUtility.EditorSpec[] specs)
        {
            installedEditorIds.Clear();
            foreach (var spec in specs)
                if (ConduitSetupWizardUtility.HasUserConfigurationFile(spec))
                    installedEditorIds.Add(spec.Id);
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

        static string GetConfiguredExecutablePath(
            ConduitSetupWizardUtility.EditorSpec[] specs,
            ConduitSettings settings
        )
        {
            var location = GetConfigurationLocation(specs, settings);
            if (settings.SelectedEditorId.Length > 0)
            {
                var spec = GetSelectedSpec(specs, settings.SelectedEditorId);
                if (ConduitSetupWizardUtility.TryGetConfiguredExecutablePath(
                        spec,
                        location,
                        out var scopedExecutablePath,
                        out _
                    ))
                    return scopedExecutablePath;
            }

            return ConduitSetupWizardUtility.TryGetAnyConfiguredExecutablePath(
                location,
                out var executablePath,
                out _
            )
                ? executablePath
                : string.Empty;
        }

        static ConduitSetupWizardUtility.EditorSpec GetSelectedSpec(
            ConduitSetupWizardUtility.EditorSpec[] specs,
            string selectedId
        )
        {
            foreach (var spec in specs)
                if (spec.Id == selectedId)
                    return spec;

            throw new InvalidOperationException($"Unknown editor '{selectedId}'.");
        }

        static ConduitSetupWizardUtility.ConfigurationLocation GetConfigurationLocation(
            ConduitSetupWizardUtility.EditorSpec spec,
            ConduitSettings settings
        )
            => ConduitSetupWizardUtility.SupportsProjectConfiguration(spec)
                ? settings.ConfigurationLocation
                : ConduitSetupWizardUtility.ConfigurationLocation.User;

        static ConduitSetupWizardUtility.ConfigurationLocation GetConfigurationLocation(
            ConduitSetupWizardUtility.EditorSpec[] specs,
            ConduitSettings settings
        )
            => settings.SelectedEditorId.Length == 0
                ? settings.ConfigurationLocation
                : GetConfigurationLocation(
                    GetSelectedSpec(specs, settings.SelectedEditorId),
                    settings
                );

        static string ShortHash(string hash)
            => hash.Length > 8 ? hash[..8] : hash;

    }
}
#endif
