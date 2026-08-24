#nullable enable

using System;
using System.IO;

namespace Conduit
{
    static class SetupActionEvaluator
    {
        internal static SetupButtonModel EvaluateConfigureButton(
            EditorClientSpec spec,
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateConfigureButton(
                spec,
                EditorConfigurationPaths.GetDefaultConfigurationLocation(spec),
                serverExecutablePath,
                isRunning,
                hasError
            );

        internal static SetupButtonModel EvaluateConfigureButton(
            EditorClientSpec spec,
            SetupConfigurationLocation location,
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
        {
            if (isRunning)
                return new()
                {
                    State = SetupActionState.Running,
                    Label = $"Configuring {spec.DisplayName}...",
                    Hint = "Writing Conduit's MCP server entry while preserving unrelated editor settings.",
                };

            if (hasError)
                return new()
                {
                    State = SetupActionState.Error,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        "The previous configuration attempt failed. " +
                        "The Console contains the config path and full error.",
                };

            if (spec.RequireUnambiguousConfigPath && EditorConfigurationPaths.CountExistingConfigPaths(spec, location) > 1)
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"More than one {spec.DisplayName} MCP config file exists. " +
                        "Open the editor's raw MCP config, keep the active path, and return here.",
                };

            if (EditorConfiguration.TryGetConfiguredExecutablePath(
                    spec,
                    location,
                    out var configuredExecutablePath,
                    out var configuredConfigPath
                )
                && (serverExecutablePath.Length == 0
                    || SetupPathUtility.PathsEqual(configuredExecutablePath, serverExecutablePath))
                && EditorConfiguration.IsEditorConfigured(spec, configuredConfigPath, configuredExecutablePath))
                return new()
                {
                    State = SetupActionState.Success,
                    Label = $"{spec.DisplayName} configured",
                    Hint =
                        $"{spec.DisplayName} is configured to launch the MCP server at `{configuredExecutablePath}`. " +
                        $"Configuration file: `{configuredConfigPath}`.",
                };

            string? configPath = EditorConfigurationPaths.GetWriteConfigPath(spec, location);
            if (configPath is null)
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"Conduit does not know where {spec.DisplayName} stores its MCP configuration " +
                        "on this operating system. Use the manual setup instructions above.",
                };

            if (serverExecutablePath.Length > 0 && EditorConfiguration.IsEditorConfigured(spec, configPath, serverExecutablePath))
                return new()
                {
                    State = SetupActionState.Success,
                    Label = $"{spec.DisplayName} configured",
                    Hint =
                        $"{spec.DisplayName} is configured in `{configPath}` " +
                        $"to launch `{serverExecutablePath}`.",
                };

            if (spec.CreateOnlyConfig && File.Exists(configPath))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"`{configPath}` already exists and may contain comments. " +
                        "Conduit will not rewrite it; use the manual setup instructions to add the server " +
                        "without losing formatting.",
                };

            if (string.Equals(Path.GetExtension(configPath), ".jsonc", StringComparison.OrdinalIgnoreCase))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"`{configPath}` is a JSONC file. Conduit will not rewrite it and discard its comments; " +
                        "use the manual setup instructions instead.",
                };

            if (spec.Format == EditorConfigurationFormat.Json
                && File.Exists(configPath)
                && ConduitSimpleJson.ContainsComments(File.ReadAllText(configPath)))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"`{configPath}` contains comments. Conduit will not rewrite it and discard them; " +
                        "use the manual setup instructions instead.",
                };

            if (serverExecutablePath.Length == 0)
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =  $"Install the MCP server first. Conduit can then be registered in `{configPath}`.",
                };

            if (!spec.CreateMissingConfig && !File.Exists(configPath))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"{spec.DisplayName} has not created `{configPath}` yet. " +
                        "Open the editor once or create that file, then return here.",
                };

            return new()
            {
                State = SetupActionState.Enabled,
                Label = $"Configure {spec.DisplayName}",
                Hint =
                    $"Add Conduit to `{configPath}` and point it at `{serverExecutablePath}`. " +
                    "Existing unrelated settings will be preserved.",
            };
        }

        internal static SetupButtonModel EvaluateCodexPermissionsButton(
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateCodexPermissionsButton(
                EditorConfigurationPaths.GetDefaultConfigurationLocation(EditorClientCatalog.FindEditorSpec("codex")),
                serverExecutablePath,
                isRunning,
                hasError
            );

        internal static SetupButtonModel EvaluateCodexPermissionsButton(
            SetupConfigurationLocation location,
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
        {
            if (isRunning)
                return new()
                {
                    State = SetupActionState.Running,
                    Label = "Configuring tool permissions...",
                    Hint = "Adding approval entries for Conduit's Unity tools.",
                };

            if (hasError)
                return new()
                {
                    State = SetupActionState.Error,
                    Label = "Configure tool permissions",
                    Hint =
                        "The previous permissions update failed. " +
                        "Check the Console for the config path and full error.",
                };

            var spec = EditorClientCatalog.FindEditorSpec("codex");
            string? configPath = EditorConfigurationPaths.GetWriteConfigPath(spec, location);
            if (configPath is null || !File.Exists(configPath))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = "Configure tool permissions",
                    Hint = "Configure Codex above before adding automatic tool approvals.",
                };

            if (CodexTomlConfiguration.HasToolPermissions(configPath))
                return new()
                {
                    State = SetupActionState.Success,
                    Label = "Tool permissions configured",
                    Hint = "Codex will approve Conduit's Unity tools automatically.",
                };

            if (serverExecutablePath.Length == 0 && !EditorConfiguration.IsEditorConfigured(spec, configPath, string.Empty))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = "Configure tool permissions",
                    Hint = "Configure Codex to use the MCP server before adding automatic tool approvals.",
                };

            return new()
            {
                State = SetupActionState.Enabled,
                Label = "Configure tool permissions",
                Hint =
                    "Add automatic approval entries for Conduit's Unity tools. " +
                    "Existing settings will be preserved.",
            };
        }

        internal static SetupButtonModel EvaluateDownloadButton(
            string serverExecutablePath,
            string configuredExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateDownloadButtonCore(
                SetupConfigurationLocation.Project,
                ServerExecutableLocator.GetEffectiveExecutablePath(
                    serverExecutablePath,
                    configuredExecutablePath
                ),
                isRunning,
                hasError
            );

        internal static SetupButtonModel EvaluateDownloadButton(
            SetupConfigurationLocation location,
            string serverExecutablePath,
            string configuredExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateDownloadButtonCore(
                location,
                ServerExecutableLocator.GetEffectiveExecutablePath(
                    location,
                    serverExecutablePath,
                    configuredExecutablePath
                ),
                isRunning,
                hasError
            );

        internal static SetupButtonModel EvaluateDownloadButtonCore(
            SetupConfigurationLocation location,
            string executablePath,
            bool isRunning,
            bool hasError
        )
        {
            // immutable nix paths can only be updated by rebuilding the configuration that owns them.
            if (ServerInstallation.IsNixOsManagedExecutablePath(executablePath))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = "MCP server managed by NixOS",
                    Hint =
                        $"Conduit is managed by NixOS at `{executablePath}`. " +
                        "Update the package through your NixOS configuration instead of this wizard.",
                    IsOutdated = false,
                };

            bool isOutdated = ServerVersionProbe.ShouldOfferServerUpdate(
                executablePath,
                out var installedVersion,
                out var packageVersion
            );

            if (isRunning)
                return new()
                {
                    State = SetupActionState.Running,
                    Label = executablePath.Length > 0 ? "Updating the MCP server..." : "Downloading the MCP server...",
                    Hint = executablePath.Length > 0
                        ? $"Downloading the latest server release and replacing `{executablePath}`."
                        : location == SetupConfigurationLocation.User
                            ? $"Downloading the MCP server for this operating system to " +
                              $"`{ServerInstallation.GetUserInstalledExecutablePath()}`."
                            : "Downloading the Windows and Linux server binaries.",
                    IsOutdated = isOutdated,
                };

            if (hasError)
                return new()
                {
                    State = SetupActionState.Error,
                    Label = executablePath.Length > 0 ? "Update the MCP server" : "Download the MCP server",
                    Hint =
                        "The previous server download failed. " +
                        "The Console contains the full error and the destination path.",
                    IsOutdated = isOutdated,
                };

            if (executablePath.Length > 0
                && !ServerVersionProbe.TryGetExecutableVersion(executablePath, out _))
            {
                if (!ServerInstallation.CanDownloadServer(out var unsupportedReason))
                    return new()
                    {
                        State = SetupActionState.Error,
                        Label = "MCP server reinstall is unavailable on this platform",
                        Hint = unsupportedReason,
                        IsOutdated = isOutdated,
                    };

                if (!ServerInstallation.CanAutomaticallyUpdateServer(
                        executablePath,
                        out var updateReason
                    ))
                    return new()
                    {
                        State = SetupActionState.Error,
                        Label = "MCP server binary cannot be reinstalled automatically",
                        Hint = updateReason,
                        IsOutdated = isOutdated,
                    };

                return new()
                {
                    State = SetupActionState.Enabled,
                    Label = "Reinstall the MCP server",
                    Hint =
                        $"The server at `{executablePath}` could not report its version. " +
                        "Download a fresh copy and replace it in place.",
                    IsOutdated = isOutdated,
                };
            }

            if (isOutdated)
            {
                if (!ServerInstallation.CanAutomaticallyUpdateServer(
                        executablePath,
                        out var updateReason
                    ))
                    return new()
                    {
                        State = SetupActionState.Error,
                        Label = "MCP server binary is outdated but not writeable",
                        Hint = updateReason,
                        IsOutdated = true,
                    };

                if (!ServerInstallation.CanDownloadServer(out var unsupportedReason))
                    return new()
                    {
                        State = SetupActionState.Error,
                        Label = "MCP server update is unavailable on this platform",
                        Hint = unsupportedReason,
                        IsOutdated = true,
                    };

                return new()
                {
                    State = SetupActionState.Enabled,
                    Label = "Update the MCP server",
                    Hint =
                        $"The installed server version {installedVersion} is older than " +
                        $"the Unity package version {packageVersion}. Replace `{executablePath}` in place.",
                    IsOutdated = true,
                };
            }

            if (executablePath.Length > 0)
            {
                string hint = $"The MCP server is installed in: `{executablePath}`.";

                return new()
                {
                    State = SetupActionState.Success,
                    Label = "MCP server installed",
                    Hint = hint,
                    IsOutdated = isOutdated,
                };
            }

            if (!ServerInstallation.CanDownloadServer(out var reason))
                return new()
                {
                    State = SetupActionState.Disabled,
                    Label = "Download the MCP server",
                    Hint = reason,
                    IsOutdated = isOutdated,
                };

            return new()
            {
                State = SetupActionState.Enabled,
                Label = "Download the MCP server",
                Hint = location == SetupConfigurationLocation.User
                    ? $"Download only the MCP server binary for this operating system to `{ServerInstallation.GetUserInstalledExecutablePath()}`."
                    : $"Download the Windows and Linux binaries to the project directory: `{ServerInstallation.GetInstallDirectoryPath()}`. ",
                IsOutdated = isOutdated,
            };
        }
    }
}
