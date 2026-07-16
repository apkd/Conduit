#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        public static ButtonModel EvaluateConfigureButton(
            EditorSpec spec,
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateConfigureButton(
                spec,
                GetDefaultConfigurationLocation(spec),
                serverExecutablePath,
                isRunning,
                hasError
            );

        public static ButtonModel EvaluateConfigureButton(
            EditorSpec spec,
            ConfigurationLocation location,
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
        {
            if (isRunning)
                return new()
                {
                    State = ActionState.Running,
                    Label = $"Configuring {spec.DisplayName}...",
                    Hint = "Writing Conduit's MCP server entry while preserving unrelated editor settings.",
                };

            if (hasError)
                return new()
                {
                    State = ActionState.Error,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        "The previous configuration attempt failed. " +
                        "The Console contains the config path and full error.",
                };

            if (spec.RequireUnambiguousConfigPath && CountExistingConfigPaths(spec, location) > 1)
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"More than one {spec.DisplayName} MCP config file exists. " +
                        "Open the editor's raw MCP config, keep the active path, and return here.",
                };

            if (TryGetConfiguredExecutablePath(
                    spec,
                    location,
                    out var configuredExecutablePath,
                    out var configuredConfigPath
                )
                && (serverExecutablePath.Length == 0
                    || PathsEqual(configuredExecutablePath, serverExecutablePath))
                && IsEditorConfigured(spec, configuredConfigPath, configuredExecutablePath))
                return new()
                {
                    State = ActionState.Success,
                    Label = $"{spec.DisplayName} configured",
                    Hint =
                        $"{spec.DisplayName} is configured to launch the MCP server at `{configuredExecutablePath}`. " +
                        $"Configuration file: `{configuredConfigPath}`.",
                };

            string? configPath = GetWriteConfigPath(spec, location);
            if (configPath is null)
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"Conduit does not know where {spec.DisplayName} stores its MCP configuration " +
                        "on this operating system. Use the manual setup instructions above.",
                };

            if (serverExecutablePath.Length > 0 && IsEditorConfigured(spec, configPath, serverExecutablePath))
                return new()
                {
                    State = ActionState.Success,
                    Label = $"{spec.DisplayName} configured",
                    Hint =
                        $"{spec.DisplayName} is configured in `{configPath}` " +
                        $"to launch `{serverExecutablePath}`.",
                };

            if (spec.CreateOnlyConfig && File.Exists(configPath))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"`{configPath}` already exists and may contain comments. " +
                        "Conduit will not rewrite it; use the manual setup instructions to add the server " +
                        "without losing formatting.",
                };

            if (string.Equals(Path.GetExtension(configPath), ".jsonc", StringComparison.OrdinalIgnoreCase))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"`{configPath}` is a JSONC file. Conduit will not rewrite it and discard its comments; " +
                        "use the manual setup instructions instead.",
                };

            if (spec.Format == ConfigFormat.Json
                && File.Exists(configPath)
                && ConduitSimpleJson.ContainsComments(File.ReadAllText(configPath)))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"`{configPath}` contains comments. Conduit will not rewrite it and discard them; " +
                        "use the manual setup instructions instead.",
                };

            if (serverExecutablePath.Length == 0)
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =  $"Install the MCP server first. Conduit can then be registered in `{configPath}`.",
                };

            if (!spec.CreateMissingConfig && !File.Exists(configPath))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = $"Configure {spec.DisplayName}",
                    Hint =
                        $"{spec.DisplayName} has not created `{configPath}` yet. " +
                        "Open the editor once or create that file, then return here.",
                };

            return new()
            {
                State = ActionState.Enabled,
                Label = $"Configure {spec.DisplayName}",
                Hint =
                    $"Add Conduit to `{configPath}` and point it at `{serverExecutablePath}`. " +
                    "Existing unrelated settings will be preserved.",
            };
        }

        public static ButtonModel EvaluateCodexPermissionsButton(
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
            => EvaluateCodexPermissionsButton(
                GetDefaultConfigurationLocation(FindEditorSpec("codex")),
                serverExecutablePath,
                isRunning,
                hasError
            );

        public static ButtonModel EvaluateCodexPermissionsButton(
            ConfigurationLocation location,
            string serverExecutablePath,
            bool isRunning,
            bool hasError
        )
        {
            if (isRunning)
                return new()
                {
                    State = ActionState.Running,
                    Label = "Configuring tool permissions...",
                    Hint = "Adding approval entries for Conduit's Unity tools.",
                };

            if (hasError)
                return new()
                {
                    State = ActionState.Error,
                    Label = "Configure tool permissions",
                    Hint =
                        "The previous permissions update failed. " +
                        "Check the Console for the config path and full error.",
                };

            var spec = FindEditorSpec("codex");
            string? configPath = GetWriteConfigPath(spec, location);
            if (configPath is null || !File.Exists(configPath))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = "Configure tool permissions",
                    Hint = "Configure Codex above before adding automatic tool approvals.",
                };

            if (HasCodexPermissions(configPath))
                return new()
                {
                    State = ActionState.Success,
                    Label = "Tool permissions configured",
                    Hint = "Codex will approve Conduit's Unity tools automatically.",
                };

            if (serverExecutablePath.Length == 0 && !IsEditorConfigured(spec, configPath, string.Empty))
                return new()
                {
                    State = ActionState.Disabled,
                    Label = "Configure tool permissions",
                    Hint = "Configure Codex to use the MCP server before adding automatic tool approvals.",
                };

            return new()
            {
                State = ActionState.Enabled,
                Label = "Configure tool permissions",
                Hint =
                    "Add automatic approval entries for Conduit's Unity tools. " +
                    "Existing settings will be preserved.",
            };
        }

        public static bool SupportsProjectConfiguration(EditorSpec spec)
            => spec.ResolveProjectConfigPath is not null;

        public static ConfigurationLocation GetDefaultConfigurationLocation(EditorSpec spec)
            => SupportsProjectConfiguration(spec)
                ? ConfigurationLocation.Project
                : ConfigurationLocation.User;

        public static ConfigurationLocation GetPreferredConfigurationLocation(
            EditorSpec spec,
            ConfigurationLocation fallback
        )
        {
            bool hasProjectConfiguration = TryGetConfiguredExecutablePath(
                spec,
                ConfigurationLocation.Project,
                out _,
                out _
            );
            bool hasUserConfiguration = TryGetConfiguredExecutablePath(
                spec,
                ConfigurationLocation.User,
                out _,
                out _
            );
            return (hasProjectConfiguration, hasUserConfiguration) switch
            {
                (true, false) => ConfigurationLocation.Project,
                (false, true) => ConfigurationLocation.User,
                _ => fallback,
            };
        }

        public static string? GetConfigPath(EditorSpec spec)
            => GetConfigPath(spec, GetDefaultConfigurationLocation(spec));

        public static string? GetConfigPath(EditorSpec spec, ConfigurationLocation location)
        {
            var context = CreatePathContext();
            return GetConfigPathResolver(spec, location)?.Invoke(context);
        }

        public static string? GetDisplayConfigPath(EditorSpec spec)
            => GetDisplayConfigPath(spec, GetDefaultConfigurationLocation(spec));

        public static string? GetDisplayConfigPath(EditorSpec spec, ConfigurationLocation location)
        {
            if (TryGetConfiguredExecutablePath(spec, location, out _, out var configuredConfigPath))
                return configuredConfigPath;

            var configPaths = GetConfigPaths(spec, location);
            foreach (string configPath in configPaths)
                if (File.Exists(configPath))
                    return configPath;

            return GetConfigPath(spec, location) ?? (configPaths.Length > 0 ? configPaths[0] : null);
        }

        public static void ConfigureEditor(EditorSpec spec, string serverExecutablePath)
            => ConfigureEditor(spec, GetDefaultConfigurationLocation(spec), serverExecutablePath);

        public static void ConfigureEditor(
            EditorSpec spec,
            ConfigurationLocation location,
            string serverExecutablePath
        )
        {
            if (serverExecutablePath.Length == 0)
                throw new InvalidOperationException("Server executable path was not set.");

            if (!File.Exists(serverExecutablePath))
                throw new InvalidOperationException($"Server executable '{serverExecutablePath}' does not exist.");

            string configPath = GetWriteConfigPath(spec, location)
                                ?? throw new InvalidOperationException(
                                    $"Editor '{spec.DisplayName}' is not supported on this OS."
                                );

            if (!spec.CreateMissingConfig && !File.Exists(configPath))
                throw new InvalidOperationException($"Config file '{configPath}' does not exist.");

            if (spec.RequireUnambiguousConfigPath && CountExistingConfigPaths(spec, location) > 1)
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

            if (spec.Format == ConfigFormat.Json
                && File.Exists(configPath)
                && ConduitSimpleJson.ContainsComments(File.ReadAllText(configPath)))
                throw new InvalidOperationException(
                    $"Config file '{configPath}' contains comments and cannot be updated without discarding them. " +
                    "Use the manual setup instructions."
                );

            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            switch (spec.Format)
            {
                case ConfigFormat.Json:
                    WriteJsonConfig(spec, configPath, serverExecutablePath);
                    break;
                case ConfigFormat.Toml:
                    WriteCodexConfig(configPath, serverExecutablePath);
                    break;
            }
        }

        public static void ConfigureCodexPermissions()
            => ConfigureCodexPermissions(GetDefaultConfigurationLocation(FindEditorSpec("codex")));

        public static void ConfigureCodexPermissions(ConfigurationLocation location)
        {
            string configPath = GetWriteConfigPath(FindEditorSpec("codex"), location)
                                ?? throw new InvalidOperationException("Codex is not supported on this OS.");

            ConfigureCodexPermissions(configPath);
        }

        internal static void ConfigureCodexPermissions(string configPath)
        {
            if (!File.Exists(configPath))
                throw new InvalidOperationException($"Config file '{configPath}' does not exist.");

            var document = ReadTomlDocument(configPath);
            foreach (string tool in codexApprovedTools)
                SetTomlKey(
                    document,
                    "mcp_servers.unity",
                    $"tools.{tool}.approval_mode",
                    "\"approve\""
                );

            WriteTomlDocument(configPath, document);
        }

        public static bool IsEditorConfigured(
            EditorSpec spec,
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
                    ConfigFormat.Json => IsJsonConfigApplied(spec, configPath, expectedServerExecutablePath),
                    ConfigFormat.Toml => IsCodexConfigApplied(configPath, expectedServerExecutablePath),
                    _ => false,
                };
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetConfiguredExecutablePath(
            EditorSpec spec,
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
                    ConfigFormat.Json => TryGetConfiguredJsonExecutablePath(spec, configPath, out executablePath),
                    ConfigFormat.Toml => TryGetConfiguredCodexExecutablePath(configPath, out executablePath),
                    _ => false,
                };
            }
            catch
            {
                executablePath = string.Empty;
                return false;
            }
        }

        public static bool TryGetConfiguredExecutablePath(
            EditorSpec spec,
            out string executablePath,
            out string configPath
        )
            => TryGetConfiguredExecutablePath(
                spec,
                GetAllConfigPaths(spec),
                out executablePath,
                out configPath
            );

        public static bool TryGetConfiguredExecutablePath(
            EditorSpec spec,
            ConfigurationLocation location,
            out string executablePath,
            out string configPath
        )
            => TryGetConfiguredExecutablePath(
                spec,
                GetConfigPaths(spec, location),
                out executablePath,
                out configPath
            );

        static bool TryGetConfiguredExecutablePath(
            EditorSpec spec,
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
            => TryGetAnyConfiguredExecutablePath(editorSpecs, out executablePath, out configPath);

        internal static bool TryGetAnyConfiguredExecutablePath(
            ConfigurationLocation location,
            out string executablePath,
            out string configPath
        )
            => TryGetAnyConfiguredExecutablePath(
                editorSpecs,
                location,
                out executablePath,
                out configPath
            );

        internal static bool TryGetAnyConfiguredExecutablePath(
            EditorSpec[] specs,
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
            EditorSpec[] specs,
            ConfigurationLocation location,
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

        public static bool HasCodexPermissions(string configPath)
        {
            if (!File.Exists(configPath))
                return false;

            try
            {
                var document = ReadTomlDocument(configPath);
                foreach (string tool in codexApprovedTools)
                    if (!string.Equals(
                            GetTomlValue(document, "mcp_servers.unity", $"tools.{tool}.approval_mode"),
                            "\"approve\"",
                            StringComparison.Ordinal))
                        return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        static PathContext CreatePathContext()
            => new()
            {
                ProjectRoot = ConduitAssetPathUtility.GetProjectRootPath(),
                UserHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };

        static string Combine(params string[] segments)
        {
            if (segments.Length == 0)
                return string.Empty;

            string path = segments[0];
            for (int index = 1, count = segments.Length; index < count; ++index)
                path = Path.Combine(path, segments[index]);

            return Path.GetFullPath(path);
        }

        static Func<PathContext, string?>? GetConfigPathResolver(
            EditorSpec spec,
            ConfigurationLocation location
        )
            => location switch
            {
                ConfigurationLocation.Project => spec.ResolveProjectConfigPath,
                ConfigurationLocation.User => spec.ResolveUserConfigPath,
                _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
            };

        static string[] GetConfigPaths(EditorSpec spec, ConfigurationLocation location)
        {
            var context = CreatePathContext();
            using var pooledList = ConduitUtility.GetPooledList<string>(out var paths);
            using var pooledSet = ConduitUtility.GetPooledSet<string>(out var uniquePaths);

            var configPathsResolver = location switch
            {
                ConfigurationLocation.Project => spec.ResolveProjectConfigPaths,
                ConfigurationLocation.User => spec.ResolveUserConfigPaths,
                _ => throw new ArgumentOutOfRangeException(nameof(location), location, null),
            };
            if (configPathsResolver?.Invoke(context) is { } candidatePaths)
                foreach (string? path in candidatePaths)
                    AddPath(path);
            AddPath(GetConfigPathResolver(spec, location)?.Invoke(context));
            return paths.ToArray();

            void AddPath(string? path)
            {
                if (path is not { Length: > 0 } || !uniquePaths.Add(path))
                    return;

                paths.Add(path);
            }
        }

        internal static bool HasUserConfigurationFile(EditorSpec spec)
        {
            foreach (string path in GetConfigPaths(spec, ConfigurationLocation.User))
                if (File.Exists(path))
                    return true;

            return false;
        }

        static string[] GetAllConfigPaths(EditorSpec spec)
        {
            using var pooledList = ConduitUtility.GetPooledList<string>(out var paths);
            using var pooledSet = ConduitUtility.GetPooledSet<string>(out var uniquePaths);

            AddPaths(GetConfigPaths(spec, ConfigurationLocation.Project));
            AddPaths(GetConfigPaths(spec, ConfigurationLocation.User));
            return paths.ToArray();

            void AddPaths(string[] candidates)
            {
                foreach (string path in candidates)
                    if (uniquePaths.Add(path))
                        paths.Add(path);
            }
        }

        static string? GetWriteConfigPath(EditorSpec spec, ConfigurationLocation location)
        {
            foreach (string configPath in GetConfigPaths(spec, location))
                if (File.Exists(configPath))
                    return configPath;

            return GetConfigPath(spec, location);
        }

        static int CountExistingConfigPaths(EditorSpec spec, ConfigurationLocation location)
        {
            int count = 0;
            foreach (string path in GetConfigPaths(spec, location))
                if (File.Exists(path))
                    count++;

            return count;
        }

        static void WriteJsonConfig(EditorSpec spec, string configPath, string serverExecutablePath)
        {
            var document = ConduitSimpleJson.ParseObject(File.Exists(configPath) ? File.ReadAllText(configPath) : "{}");
            var entry = ConduitSimpleJson.EnsureObject(
                ConduitSimpleJson.EnsureObject(ConduitSimpleJson.Root(document), spec.BodyPath),
                "unity");

            if (spec.UseCommandArray)
            {
                ConduitSimpleJson.SetStringArray(entry, "command", serverExecutablePath);
                ConduitSimpleJson.Remove(entry, "args");
            }
            else
            {
                ConduitSimpleJson.SetString(entry, "command", serverExecutablePath);
                ConduitSimpleJson.SetStringArray(entry, "args");
            }

            if (spec.TypeValue is null)
                ConduitSimpleJson.Remove(entry, "type");
            else
                ConduitSimpleJson.SetString(entry, "type", spec.TypeValue);

            if (spec.EnabledValue is { } enabled)
                ConduitSimpleJson.SetBool(entry, "enabled", enabled);

            if (spec.DisabledValue is { } disabled)
                ConduitSimpleJson.SetBool(entry, "disabled", disabled);

            if (spec.IncludeAllTools)
                ConduitSimpleJson.SetStringArray(entry, "tools", "*");

            foreach (string key in spec.RemoveKeys)
                ConduitSimpleJson.Remove(entry, key);

            File.WriteAllText(configPath, ConduitSimpleJson.Serialize(document));
        }

        static bool IsJsonConfigApplied(EditorSpec spec, string configPath, string expectedServerExecutablePath)
        {
            if (ConduitSimpleJson.GetObject(
                    ConduitSimpleJson.Root(ConduitSimpleJson.ParseObject(File.ReadAllText(configPath))),
                    spec.BodyPath
                ) is not { } body)
                return false;

            // user-named MCP entries require matching executable identity instead of the conventional "unity" key
            foreach (var pair in body.Object.Properties)
            {
                if (pair.Value is not ConduitSimpleJson.JsonObjectValue)
                    continue;

                var entry = ConduitSimpleJson.GetObject(body, pair.Key);
                if (IsEntryApplied(entry))
                    return true;
            }

            return false;

            bool IsEntryApplied(ConduitSimpleJson.JsonObject? entry)
            {
                if (entry is null)
                    return false;

                if (spec.TypeValue is not null)
                {
                    string? type = ConduitSimpleJson.GetString(entry, "type");
                    if (!string.Equals(type, spec.TypeValue, StringComparison.Ordinal)
                        && !(spec.TypeOptionalWhenReading && type is null))
                        return false;
                }

                if (spec.EnabledValue is { } enabled)
                {
                    bool? configuredEnabled = ConduitSimpleJson.GetBool(entry, "enabled");
                    if (configuredEnabled != enabled
                        && !(spec.StateOptionalWhenReading && configuredEnabled is null))
                        return false;
                }

                if (spec.DisabledValue is { } disabled)
                {
                    bool? configuredDisabled = ConduitSimpleJson.GetBool(entry, "disabled");
                    if (configuredDisabled != disabled
                        && !(spec.StateOptionalWhenReading && configuredDisabled is null))
                        return false;
                }

                if (spec.IncludeAllTools && ConduitSimpleJson.GetFirstString(entry, "tools") != "*")
                    return false;

                string? command = spec.UseCommandArray
                    ? ConduitSimpleJson.GetFirstString(entry, "command")
                    : ConduitSimpleJson.GetString(entry, "command");

                return CommandMatches(command, expectedServerExecutablePath);
            }
        }

        static bool TryGetConfiguredJsonExecutablePath(EditorSpec spec, string configPath, out string executablePath)
        {
            executablePath = string.Empty;

            if (ConduitSimpleJson.GetObject(
                    ConduitSimpleJson.Root(ConduitSimpleJson.ParseObject(File.ReadAllText(configPath))),
                    spec.BodyPath
                ) is not { } body)
                return false;

            foreach (var pair in body.Object.Properties)
            {
                if (pair.Value is not ConduitSimpleJson.JsonObjectValue)
                    continue;

                var entry = ConduitSimpleJson.GetObject(body, pair.Key);
                string? command = spec.UseCommandArray
                    ? ConduitSimpleJson.GetFirstString(entry, "command")
                    : ConduitSimpleJson.GetString(entry, "command")
                      ?? ConduitSimpleJson.GetFirstString(entry, "command");

                if (!TryResolveConfiguredExecutable(command, out executablePath))
                    continue;

                return true;
            }

            return false;
        }

        static bool CommandMatches(string? configuredCommand, string expectedServerExecutablePath)
        {
            if (string.IsNullOrWhiteSpace(configuredCommand))
                return false;

            configuredCommand = ToPlatformPath(configuredCommand);
            if (expectedServerExecutablePath.Length > 0)
                return TryResolveCommand(configuredCommand, out var configuredPath)
                       && TryResolveCommand(expectedServerExecutablePath, out var expectedPath)
                       && PathsEqual(configuredPath, expectedPath);

            return Path.GetFileNameWithoutExtension(configuredCommand)
                       .Contains("conduit", StringComparison.OrdinalIgnoreCase)
                   && TryResolveCommand(configuredCommand, out _);
        }

        static bool TryResolveConfiguredExecutable(string? command, out string executablePath)
        {
            executablePath = string.Empty;
            if (!CommandMatches(command, string.Empty))
                return false;

            return TryResolveCommand(command, out executablePath);
        }

        static bool TryResolveCommand(string? command, out string executablePath)
        {
            executablePath = string.Empty;
            command = ToPlatformPath(command);
            if (command is not { Length: > 0 })
                return false;

            if (Path.GetDirectoryName(command) is not { Length: > 0 })
            {
                string windowsName = Path.HasExtension(command) ? command : command + ".exe";
                command = FindOnPath(command, windowsName);
            }

            if (command is null || !File.Exists(command))
                return false;

            executablePath = Path.GetFullPath(command);
            return true;
        }

        static void WriteCodexConfig(string configPath, string serverExecutablePath)
        {
            var document = ReadTomlDocument(configPath);
            SetTomlKey(document, "mcp_servers.unity", "enabled", "true");
            SetTomlKey(document, "mcp_servers.unity", "command", QuoteToml(serverExecutablePath));
            SetTomlKey(document, "mcp_servers.unity", "args", "[]");
            SetTomlKey(document, "mcp_servers.unity", "tool_timeout_sec", "300");
            RemoveTomlKey(document, "mcp_servers.unity", "url");
            RemoveTomlKey(document, "mcp_servers.unity", "type");
            RemoveTomlKey(document, "mcp_servers.unity", "bearer_token");
            RemoveTomlKey(document, "mcp_servers.unity", "bearer_token_env_var");
            RemoveTomlKey(document, "mcp_servers.unity", "http_headers");
            RemoveTomlKey(document, "mcp_servers.unity", "env_http_headers");
            RemoveTomlKey(document, "mcp_servers.unity", "oauth_resource");
            WriteTomlDocument(configPath, document);
        }

        static bool IsCodexConfigApplied(string configPath, string expectedServerExecutablePath)
        {
            var document = ReadTomlDocument(configPath);
            string? enabled = GetTomlValue(document, "mcp_servers.unity", "enabled");
            return (enabled is null || enabled == "true")
                   && CommandMatches(
                       UnquoteToml(GetTomlValue(document, "mcp_servers.unity", "command")),
                       expectedServerExecutablePath
                   );
        }

        static bool TryGetConfiguredCodexExecutablePath(string configPath, out string executablePath)
        {
            executablePath = string.Empty;
            string? currentTable = null;
            foreach (string rawLine in File.ReadLines(configPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    currentTable = line[1..^1].Trim();
                    continue;
                }

                if (currentTable is null
                    || !currentTable.StartsWith("mcp_servers.", StringComparison.Ordinal))
                    continue;

                int separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0 || line[..separatorIndex].Trim() != "command")
                    continue;

                if (TryResolveConfiguredExecutable(
                        UnquoteToml(line[(separatorIndex + 1)..].Trim()),
                        out executablePath
                    ))
                    return true;
            }

            return false;
        }

        // codex config can contain TOML constructs outside this package's scope
        // manage only [mcp_servers.unity] as lines so every unrelated setting remains byte-for-byte intact
        static TomlDocument ReadTomlDocument(string path)
        {
            var document = new TomlDocument
            {
                Lines = File.Exists(path)
                    ? new List<string>(File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
                    : new(),
            };

            ParseTomlTable(document, "mcp_servers.unity");
            return document;
        }

        static void ParseTomlTable(TomlDocument document, string tableName)
        {
            document.TableStart = -1;
            document.TableEnd = -1;
            document.Entries.Clear();

            string header = $"[{tableName}]";
            for (int index = 0, count = document.Lines.Count; index < count; ++index)
            {
                if (!string.Equals(document.Lines[index].Trim(), header, StringComparison.Ordinal))
                    continue;

                document.TableStart = index;
                document.TableEnd = document.Lines.Count;
                for (int lineIndex = index + 1, lineCount = document.Lines.Count;
                     lineIndex < lineCount;
                     ++lineIndex)
                {
                    string trimmed = document.Lines[lineIndex].Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        document.TableEnd = lineIndex;
                        break;
                    }

                    int separatorIndex = document.Lines[lineIndex].IndexOf('=');
                    if (separatorIndex <= 0)
                        continue;

                    document.Entries[document.Lines[lineIndex][..separatorIndex].Trim()] = lineIndex;
                }

                return;
            }
        }

        static void SetTomlKey(TomlDocument document, string tableName, string key, string value)
        {
            EnsureTomlTable(document, tableName);
            string line = $"{key} = {value}";
            if (document.Entries.TryGetValue(key, out var index))
                document.Lines[index] = line;
            else
            {
                document.Lines.Insert(document.TableEnd, line);
                ParseTomlTable(document, tableName);
            }
        }

        static void RemoveTomlKey(TomlDocument document, string tableName, string key)
        {
            EnsureTomlTable(document, tableName, create: false);
            if (!document.Entries.TryGetValue(key, out var index))
                return;

            document.Lines.RemoveAt(index);
            ParseTomlTable(document, tableName);
        }

        static string? GetTomlValue(TomlDocument document, string tableName, string key)
        {
            EnsureTomlTable(document, tableName, create: false);
            if (!document.Entries.TryGetValue(key, out var index))
                return null;

            int separatorIndex = document.Lines[index].IndexOf('=');
            return separatorIndex < 0 ? null : document.Lines[index][(separatorIndex + 1)..].Trim();
        }

        static void EnsureTomlTable(TomlDocument document, string tableName, bool create = true)
        {
            if (document.TableStart >= 0 || !create)
                return;

            if (document.Lines.Count > 0 && document.Lines[^1].Length > 0)
                document.Lines.Add(string.Empty);

            document.Lines.Add($"[{tableName}]");
            ParseTomlTable(document, tableName);
        }

        static void WriteTomlDocument(string path, TomlDocument document)
            => File.WriteAllText(path, string.Join("\n", document.Lines).TrimEnd() + "\n");

        static string QuoteToml(string value)
            => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

        static string? UnquoteToml(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string text = value!;
            if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
                return text;

            return text[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        static string ToPlatformPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            string normalizedPath = path!.Trim().Replace('\\', '/');
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return normalizedPath;

            // windows editors can read WSL client configs, so mount paths share native identity
            if (normalizedPath.Length >= 6
                && normalizedPath[0] == '/'
                && normalizedPath[1] == 'm'
                && normalizedPath[2] == 'n'
                && normalizedPath[3] == 't'
                && normalizedPath[4] == '/'
                && char.IsLetter(normalizedPath[5])
                && (normalizedPath.Length == 6 || normalizedPath[6] == '/'))
            {
                char driveLetter = char.ToUpperInvariant(normalizedPath[5]);
                string remainder = normalizedPath.Length == 6
                    ? string.Empty
                    : normalizedPath[7..].Replace('/', '\\');
                return remainder.Length == 0 ? $"{driveLetter}:\\" : $"{driveLetter}:\\{remainder}";
            }

            return normalizedPath.Replace('/', Path.DirectorySeparatorChar);
        }

        sealed class TomlDocument
        {
            public List<string> Lines = new();
            public Dictionary<string, int> Entries = new(StringComparer.Ordinal);
            public int TableStart = -1;
            public int TableEnd = -1;
        }
    }
}
