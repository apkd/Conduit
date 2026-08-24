#nullable enable

using System;
using System.IO;

namespace Conduit
{
    static class EditorConfiguration
    {
        internal static void ConfigureEditor(EditorClientSpec spec, string serverExecutablePath)
            => ConfigureEditor(
                spec,
                EditorConfigurationPaths.GetDefaultConfigurationLocation(spec),
                serverExecutablePath
            );

        internal static void ConfigureEditor(
            EditorClientSpec spec,
            SetupConfigurationLocation location,
            string serverExecutablePath
        )
        {
            if (serverExecutablePath.Length == 0)
                throw new InvalidOperationException("Server executable path was not set.");

            if (!File.Exists(serverExecutablePath))
                throw new InvalidOperationException($"Server executable '{serverExecutablePath}' does not exist.");

            string configPath = EditorConfigurationPaths.GetWriteConfigPath(spec, location)
                                ?? throw new InvalidOperationException(
                                    $"Editor '{spec.DisplayName}' is not supported on this OS."
                                );

            if (!spec.CreateMissingConfig && !File.Exists(configPath))
                throw new InvalidOperationException($"Config file '{configPath}' does not exist.");

            if (spec.RequireUnambiguousConfigPath
                && EditorConfigurationPaths.CountExistingConfigPaths(spec, location) > 1)
                throw new InvalidOperationException(
                    $"More than one {spec.DisplayName} MCP config file exists. " +
                    "Resolve the active path before configuring Conduit."
                );

            // the JSON DOM preserves values but not comment trivia, so commented files are read-only here
            if (spec.CreateOnlyConfig && File.Exists(configPath))
                throw new InvalidOperationException(
                    $"Config file '{configPath}' may contain comments and cannot be updated safely. " +
                    "Use the manual setup instructions."
                );

            if (string.Equals(Path.GetExtension(configPath), ".jsonc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"JSONC config file '{configPath}' cannot be updated without discarding comments. " +
                    "Use the manual setup instructions."
                );

            if (spec.Format == EditorConfigurationFormat.Json
                && File.Exists(configPath)
                && ConduitSimpleJson.ContainsComments(File.ReadAllText(configPath)))
                throw new InvalidOperationException(
                    $"Config file '{configPath}' contains comments and cannot be updated without discarding them. " +
                    "Use the manual setup instructions."
                );

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            switch (spec.Format)
            {
                case EditorConfigurationFormat.Json:
                    JsonEditorConfiguration.Write(spec, configPath, serverExecutablePath);
                    break;
                case EditorConfigurationFormat.Toml:
                    CodexTomlConfiguration.WriteServer(configPath, serverExecutablePath);
                    break;
            }
        }

        internal static void ConfigureCodexPermissions()
            => ConfigureCodexPermissions(
                EditorConfigurationPaths.GetDefaultConfigurationLocation(
                    EditorClientCatalog.FindEditorSpec("codex")
                )
            );

        internal static void ConfigureCodexPermissions(SetupConfigurationLocation location)
        {
            string configPath = EditorConfigurationPaths.GetWriteConfigPath(
                                    EditorClientCatalog.FindEditorSpec("codex"),
                                    location
                                )
                                ?? throw new InvalidOperationException("Codex is not supported on this OS.");

            ConfigureCodexPermissions(configPath);
        }

        internal static void ConfigureCodexPermissions(string configPath)
        {
            if (!File.Exists(configPath))
                throw new InvalidOperationException($"Config file '{configPath}' does not exist.");

            CodexTomlConfiguration.WriteToolPermissions(configPath);
        }

        internal static bool IsEditorConfigured(
            EditorClientSpec spec,
            string? configPath,
            string expectedServerExecutablePath
        )
        {
            if (configPath is null || !File.Exists(configPath))
                return false;

            try
            {
                return spec.Format switch
                {
                    EditorConfigurationFormat.Json => JsonEditorConfiguration.IsApplied(
                        spec,
                        configPath,
                        expectedServerExecutablePath
                    ),
                    EditorConfigurationFormat.Toml => CodexTomlConfiguration.IsApplied(
                        configPath,
                        expectedServerExecutablePath
                    ),
                    _ => false,
                };
            }
            catch
            {
                return false;
            }
        }

        internal static bool TryGetConfiguredExecutablePath(
            EditorClientSpec spec,
            string? configPath,
            out string executablePath
        )
        {
            executablePath = string.Empty;
            if (configPath is null || !File.Exists(configPath))
                return false;

            try
            {
                return spec.Format switch
                {
                    EditorConfigurationFormat.Json => JsonEditorConfiguration.TryGetConfiguredExecutable(
                        spec,
                        configPath,
                        out executablePath
                    ),
                    EditorConfigurationFormat.Toml => CodexTomlConfiguration.TryGetConfiguredExecutable(
                        configPath,
                        out executablePath
                    ),
                    _ => false,
                };
            }
            catch
            {
                executablePath = string.Empty;
                return false;
            }
        }

        internal static bool TryGetConfiguredExecutablePath(
            EditorClientSpec spec,
            out string executablePath,
            out string configPath
        )
            => TryGetConfiguredExecutablePath(
                spec,
                EditorConfigurationPaths.GetAllConfigPaths(spec),
                out executablePath,
                out configPath
            );

        internal static bool TryGetConfiguredExecutablePath(
            EditorClientSpec spec,
            SetupConfigurationLocation location,
            out string executablePath,
            out string configPath
        )
            => TryGetConfiguredExecutablePath(
                spec,
                EditorConfigurationPaths.GetConfigPaths(spec, location),
                out executablePath,
                out configPath
            );

        static bool TryGetConfiguredExecutablePath(
            EditorClientSpec spec,
            string[] configPaths,
            out string executablePath,
            out string configPath
        )
        {
            executablePath = string.Empty;
            configPath = string.Empty;

            foreach (string candidatePath in configPaths)
            {
                if (!TryGetConfiguredExecutablePath(spec, candidatePath, out executablePath))
                    continue;

                configPath = candidatePath;
                return true;
            }

            return false;
        }

        internal static bool TryGetAnyConfiguredExecutablePath(out string executablePath, out string configPath)
            => TryGetAnyConfiguredExecutablePath(
                EditorClientCatalog.GetEditorSpecs(),
                out executablePath,
                out configPath
            );

        internal static bool TryGetAnyConfiguredExecutablePath(
            SetupConfigurationLocation location,
            out string executablePath,
            out string configPath
        )
            => TryGetAnyConfiguredExecutablePath(
                EditorClientCatalog.GetEditorSpecs(),
                location,
                out executablePath,
                out configPath
            );

        internal static bool TryGetAnyConfiguredExecutablePath(
            EditorClientSpec[] specs,
            out string executablePath,
            out string configPath
        )
        {
            foreach (var spec in specs)
                if (TryGetConfiguredExecutablePath(spec, out executablePath, out configPath))
                    return true;

            executablePath = string.Empty;
            configPath = string.Empty;
            return false;
        }

        internal static bool TryGetAnyConfiguredExecutablePath(
            EditorClientSpec[] specs,
            SetupConfigurationLocation location,
            out string executablePath,
            out string configPath
        )
        {
            foreach (var spec in specs)
                if (TryGetConfiguredExecutablePath(
                        spec,
                        location,
                        out executablePath,
                        out configPath
                    ))
                    return true;

            executablePath = string.Empty;
            configPath = string.Empty;
            return false;
        }
    }
}
