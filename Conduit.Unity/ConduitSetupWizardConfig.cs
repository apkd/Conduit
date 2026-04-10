#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Conduit
{
    static partial class ConduitSetupWizardUtility
    {
        public static ButtonModel EvaluateConfigureButton(EditorSpec spec, string serverExecutablePath, bool isRunning, bool hasError)
        {
            if (isRunning)
                return new() { State = ActionState.Running, Label = $"Configuring {spec.DisplayName}...", Hint = "Updating the editor MCP configuration." };

            if (hasError)
                return new() { State = ActionState.Error, Label = $"Configure {spec.DisplayName}", Hint = "The previous configuration attempt failed. Check the Console for details." };

            if (TryGetConfiguredExecutablePath(spec, out var configuredExecutablePath, out var configuredConfigPath))
                if (serverExecutablePath.Length == 0 || PathsEqual(configuredExecutablePath, serverExecutablePath))
                    return new() { State = ActionState.Success, Label = $"{spec.DisplayName} Configured", Hint = configuredConfigPath };

            var configPath = GetWriteConfigPath(spec);
            if (configPath == null)
                return new() { State = ActionState.Disabled, Label = $"Configure {spec.DisplayName}", Hint = "This editor is not supported on the current OS." };

            if (IsEditorConfigured(spec, configPath, serverExecutablePath))
                return new() { State = ActionState.Success, Label = $"{spec.DisplayName} Configured", Hint = configPath };

            if (serverExecutablePath.Length == 0)
                return new() { State = ActionState.Disabled, Label = $"Configure {spec.DisplayName}", Hint = "Download the server executable first." };

            if (!spec.CreateMissingConfig && !File.Exists(configPath))
                return new() { State = ActionState.Disabled, Label = $"Configure {spec.DisplayName}", Hint = $"Create '{configPath}' first, then rerun this step." };

            return new() { State = ActionState.Enabled, Label = $"Configure {spec.DisplayName}", Hint = configPath };
        }

        public static ButtonModel EvaluateCodexPermissionsButton(string serverExecutablePath, bool isRunning, bool hasError)
        {
            if (isRunning)
                return new() { State = ActionState.Running, Label = "Configuring Tool Permissions...", Hint = "Updating Codex approval entries." };

            if (hasError)
                return new() { State = ActionState.Error, Label = "Configure Tool Permissions", Hint = "The previous permissions update failed. Check the Console for details." };

            var spec = FindEditorSpec("codex");
            var configPath = GetWriteConfigPath(spec);
            if (configPath == null || !File.Exists(configPath))
                return new() { State = ActionState.Disabled, Label = "Configure Tool Permissions", Hint = "Create .codex/config.toml first." };

            if (HasCodexPermissions(configPath))
                return new() { State = ActionState.Success, Label = "Tool Permissions Configured", Hint = configPath };

            if (serverExecutablePath.Length == 0 && !IsEditorConfigured(spec, configPath, string.Empty))
                return new() { State = ActionState.Disabled, Label = "Configure Tool Permissions", Hint = "Configure Codex first." };

            return new() { State = ActionState.Enabled, Label = "Configure Tool Permissions", Hint = configPath };
        }

        public static string? GetConfigPath(EditorSpec spec) => spec.ResolveConfigPath(CreatePathContext());

        public static string? GetDisplayConfigPath(EditorSpec spec)
        {
            if (TryGetConfiguredExecutablePath(spec, out _, out var configuredConfigPath))
                return configuredConfigPath;

            var configPaths = GetConfigPaths(spec);
            for (var index = 0; index < configPaths.Length; index++)
                if (File.Exists(configPaths[index]))
                    return configPaths[index];

            return configPaths.Length > 0 ? configPaths[0] : null;
        }

        public static void ConfigureEditor(EditorSpec spec, string serverExecutablePath)
        {
            if (serverExecutablePath.Length == 0)
                throw new InvalidOperationException("Server executable path was not set.");

            if (!File.Exists(serverExecutablePath))
                throw new InvalidOperationException($"Server executable '{serverExecutablePath}' does not exist.");

            var configPath = GetWriteConfigPath(spec)
                ?? throw new InvalidOperationException($"Editor '{spec.DisplayName}' is not supported on this OS.");

            if (!spec.CreateMissingConfig && !File.Exists(configPath))
                throw new InvalidOperationException($"Config file '{configPath}' does not exist.");

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
        {
            var configPath = GetWriteConfigPath(FindEditorSpec("codex"))
                ?? throw new InvalidOperationException("Codex is not supported on this OS.");

            if (!File.Exists(configPath))
                throw new InvalidOperationException($"Config file '{configPath}' does not exist.");

            var document = ReadTomlDocument(configPath);
            for (var index = 0; index < codexApprovedTools.Length; index++)
                SetTomlKey(document, "mcp_servers.unity", $"tools.{codexApprovedTools[index]}.approval_mode", "\"approve\"");

            WriteTomlDocument(configPath, document);
        }

        public static bool IsEditorConfigured(EditorSpec spec, string? configPath, string expectedServerExecutablePath)
        {
            if (configPath == null || !File.Exists(configPath))
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

        public static bool TryGetConfiguredExecutablePath(EditorSpec spec, string? configPath, out string executablePath)
        {
            executablePath = string.Empty;
            if (configPath == null || !File.Exists(configPath))
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

        public static bool TryGetConfiguredExecutablePath(EditorSpec spec, out string executablePath, out string configPath)
        {
            executablePath = string.Empty;
            configPath = string.Empty;

            var configPaths = GetConfigPaths(spec);
            for (var index = 0; index < configPaths.Length; index++)
            {
                if (!TryGetConfiguredExecutablePath(spec, configPaths[index], out executablePath))
                    continue;

                configPath = configPaths[index];
                return true;
            }

            return false;
        }

        public static bool HasCodexPermissions(string configPath)
        {
            if (!File.Exists(configPath))
                return false;

            try
            {
                var document = ReadTomlDocument(configPath);
                for (var index = 0; index < codexApprovedTools.Length; index++)
                    if (!string.Equals(
                            GetTomlValue(document, "mcp_servers.unity", $"tools.{codexApprovedTools[index]}.approval_mode"),
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

            var path = segments[0];
            for (var index = 1; index < segments.Length; index++)
                path = Path.Combine(path, segments[index]);

            return Path.GetFullPath(path);
        }

        static string[] GetConfigPaths(EditorSpec spec)
        {
            var context = CreatePathContext();
            using var pooledList = ConduitUtility.GetPooledList<string>(out var paths);
            using var pooledSet = ConduitUtility.GetPooledSet<string>(out var uniquePaths);

            AddPath(spec.ResolveConfigPath(context));
            AddPath(spec.ResolveGlobalConfigPath?.Invoke(context));
            return paths.ToArray();

            void AddPath(string? path)
            {
                if (path is not { Length: > 0 } || !uniquePaths.Add(path))
                    return;

                paths.Add(path);
            }
        }

        static string? GetWriteConfigPath(EditorSpec spec)
        {
            var configPaths = GetConfigPaths(spec);
            for (var index = 0; index < configPaths.Length; index++)
                if (File.Exists(configPaths[index]))
                    return configPaths[index];

            return configPaths.Length > 0 ? configPaths[0] : null;
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

            if (spec.TypeValue == null)
                ConduitSimpleJson.Remove(entry, "type");
            else
                ConduitSimpleJson.SetString(entry, "type", spec.TypeValue);

            if (spec.EnabledValue is { } enabled)
                ConduitSimpleJson.SetBool(entry, "enabled", enabled);

            if (spec.DisabledValue is { } disabled)
                ConduitSimpleJson.SetBool(entry, "disabled", disabled);

            for (var index = 0; index < spec.RemoveKeys.Length; index++)
                ConduitSimpleJson.Remove(entry, spec.RemoveKeys[index]);

            File.WriteAllText(configPath, ConduitSimpleJson.Serialize(document));
        }

        static bool IsJsonConfigApplied(EditorSpec spec, string configPath, string expectedServerExecutablePath)
        {
            var entry = ConduitSimpleJson.GetObject(
                ConduitSimpleJson.GetObject(ConduitSimpleJson.Root(ConduitSimpleJson.ParseObject(File.ReadAllText(configPath))), spec.BodyPath),
                "unity");
            if (entry == null)
                return false;

            if (spec.TypeValue != null && !string.Equals(ConduitSimpleJson.GetString(entry, "type"), spec.TypeValue, StringComparison.Ordinal))
                return false;

            if (spec.EnabledValue is { } enabled && ConduitSimpleJson.GetBool(entry, "enabled") != enabled)
                return false;

            if (spec.DisabledValue is { } disabled && ConduitSimpleJson.GetBool(entry, "disabled") != disabled)
                return false;

            var command = spec.UseCommandArray
                ? ConduitSimpleJson.GetFirstString(entry, "command")
                : ConduitSimpleJson.GetString(entry, "command");

            return CommandMatches(command, expectedServerExecutablePath);
        }

        static bool TryGetConfiguredJsonExecutablePath(EditorSpec spec, string configPath, out string executablePath)
        {
            executablePath = string.Empty;

            var body = ConduitSimpleJson.GetObject(
                ConduitSimpleJson.Root(ConduitSimpleJson.ParseObject(File.ReadAllText(configPath))),
                spec.BodyPath);
            if (body == null)
                return false;

            foreach (var pair in body.Object.Properties)
            {
                if (pair.Value is not ConduitSimpleJson.JsonObjectValue)
                    continue;

                var entry = ConduitSimpleJson.GetObject(body, pair.Key);
                var command = spec.UseCommandArray
                    ? ConduitSimpleJson.GetFirstString(entry, "command")
                    : ConduitSimpleJson.GetString(entry, "command") ?? ConduitSimpleJson.GetFirstString(entry, "command");

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
                return Path.GetFullPath(configuredCommand) == Path.GetFullPath(ToPlatformPath(expectedServerExecutablePath));

            return Path.GetFileNameWithoutExtension(configuredCommand).Contains("conduit", StringComparison.OrdinalIgnoreCase);
        }

        static bool TryResolveConfiguredExecutable(string? command, out string executablePath)
        {
            executablePath = string.Empty;
            if (!CommandMatches(command, string.Empty))
                return false;

            command = ToPlatformPath(command);
            if (command is not { Length: > 0 } || !File.Exists(command))
                return false;

            executablePath = command;
            return true;
        }

        static void WriteCodexConfig(string configPath, string serverExecutablePath)
        {
            var document = ReadTomlDocument(configPath);
            SetTomlKey(document, "mcp_servers.unity", "enabled", "true");
            SetTomlKey(document, "mcp_servers.unity", "command", QuoteToml(serverExecutablePath));
            SetTomlKey(document, "mcp_servers.unity", "tool_timeout_sec", "300");
            RemoveTomlKey(document, "mcp_servers.unity", "url");
            RemoveTomlKey(document, "mcp_servers.unity", "type");
            RemoveTomlKey(document, "mcp_servers.unity", "startup_timeout_sec");
            WriteTomlDocument(configPath, document);
        }

        static bool IsCodexConfigApplied(string configPath, string expectedServerExecutablePath)
        {
            var document = ReadTomlDocument(configPath);
            return GetTomlValue(document, "mcp_servers.unity", "enabled") == "true"
                   && CommandMatches(UnquoteToml(GetTomlValue(document, "mcp_servers.unity", "command")), expectedServerExecutablePath);
        }

        static bool TryGetConfiguredCodexExecutablePath(string configPath, out string executablePath)
        {
            executablePath = string.Empty;
            string? currentTable = null;
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    currentTable = line[1..^1].Trim();
                    continue;
                }

                if (currentTable == null
                    || !currentTable.StartsWith("mcp_servers.", StringComparison.Ordinal)
                    || !line.StartsWith("command", StringComparison.Ordinal))
                    continue;

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0)
                    continue;

                if (TryResolveConfiguredExecutable(UnquoteToml(line[(separatorIndex + 1)..].Trim()), out executablePath))
                    return true;
            }

            return false;
        }

        /*
         * We only manage one TOML table: [mcp_servers.unity].
         * Parse just enough to replace or preserve keys inside that block while
         * leaving the rest of the file untouched.
         */
        static TomlDocument ReadTomlDocument(string path)
        {
            var document = new TomlDocument
            {
                Lines = new List<string>(File.ReadAllText(path).Replace("\r\n", "\n").Split('\n')),
            };

            ParseTomlTable(document, "mcp_servers.unity");
            return document;
        }

        static void ParseTomlTable(TomlDocument document, string tableName)
        {
            document.TableStart = -1;
            document.TableEnd = -1;
            document.Entries.Clear();

            var header = $"[{tableName}]";
            for (var index = 0; index < document.Lines.Count; index++)
            {
                if (!string.Equals(document.Lines[index].Trim(), header, StringComparison.Ordinal))
                    continue;

                document.TableStart = index;
                document.TableEnd = document.Lines.Count;
                for (var lineIndex = index + 1; lineIndex < document.Lines.Count; lineIndex++)
                {
                    var trimmed = document.Lines[lineIndex].Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        document.TableEnd = lineIndex;
                        break;
                    }

                    var separatorIndex = document.Lines[lineIndex].IndexOf('=');
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
            var line = $"{key} = {value}";
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

            var separatorIndex = document.Lines[index].IndexOf('=');
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

            var text = value!;
            if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
                return text;

            return text[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        static string ToPlatformPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var normalizedPath = path!.Trim().Replace('\\', '/');
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return normalizedPath;

            if (normalizedPath.Length >= 6
                && normalizedPath[0] == '/'
                && normalizedPath[1] == 'm'
                && normalizedPath[2] == 'n'
                && normalizedPath[3] == 't'
                && normalizedPath[4] == '/'
                && char.IsLetter(normalizedPath[5])
                && (normalizedPath.Length == 6 || normalizedPath[6] == '/'))
            {
                var driveLetter = char.ToUpperInvariant(normalizedPath[5]);
                var remainder = normalizedPath.Length == 6 ? string.Empty : normalizedPath[7..].Replace('/', '\\');
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
