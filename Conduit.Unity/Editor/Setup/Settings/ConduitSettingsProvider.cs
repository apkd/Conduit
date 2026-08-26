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
        const string SettingsPath = "Preferences/Conduit";
        const string UnfocusedGameViewDescription =
            "Saves some power by keeping the game view window unfocused during play mode runs.";
        const string LowResolutionPlayModeDescription =
            "Lowers the game view resolution to 480x320 when executing play mode commands. " +
            "This helps preserve GPU resources. Useful when running multiple Unity instances at the same time.";
        const string MuteAudioInPlayModeDescription =
            "Mutes Game View audio when executing play mode commands. " +
            "The previous mute setting is restored after returning to edit mode.";
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
        static readonly GUIContent codeEditorLabel = new("Code Editor");
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

        readonly Dictionary<SetupActionKind, bool> actionErrors = new();
        readonly HashSet<string> installedEditorIds = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> editorLabels = new(StringComparer.Ordinal);
        readonly GUIContent selectedEditorContent = new();
        SetupActionKind? runningAction;
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
                    "Audio",
                    "Mute",
                    "Sound",
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
            var specs = EditorClientCatalog.GetEditorSpecs();
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
                var spec = EditorClientCatalog.FindEditorSpec(settings.SelectedEditorId);
                if (EditorConfigurationPaths.SupportsProjectConfiguration(spec))
                    settings.SetConfigurationLocation(
                        EditorConfigurationPaths.GetPreferredConfigurationLocation(
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
            var specs = EditorClientCatalog.GetEditorSpecs();
            var location = ConduitSettingsSelection.GetConfigurationLocation(specs, settings);
            string configuredExecutablePath = ConduitSettingsSelection.GetConfiguredExecutablePath(
                specs,
                settings
            );
            string effectiveExecutablePath = ServerExecutableLocator.GetEffectiveExecutablePath(
                location,
                settings.ServerExecutablePath,
                configuredExecutablePath
            );
            // updating through UPM would bypass the package owner and change only the client.
            bool isNixOsManaged = ServerInstallation.IsNixOsManagedExecutablePath(
                effectiveExecutablePath
            );
            var downloadModel = SetupActionEvaluator.EvaluateDownloadButton(
                location,
                settings.ServerExecutablePath,
                configuredExecutablePath,
                runningAction == SetupActionKind.DownloadServer,
                HasError(SetupActionKind.DownloadServer)
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

    }
}
#endif
