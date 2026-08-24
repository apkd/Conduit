#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace Conduit
{
    static class CodexTomlConfiguration
    {
        // explicit approvals ensure newly added tools retain Codex's normal prompt policy
        static readonly string[] approvedTools =
        {
            BridgeCommandTypes.DiscardScenes,
            BridgeCommandTypes.ExecuteCode,
            BridgeCommandTypes.FindMissingScripts,
            BridgeCommandTypes.FindReferencesTo,
            BridgeCommandTypes.FromJsonOverwrite,
            BridgeCommandTypes.GetDependencies,
            BridgeCommandTypes.Help,
            BridgeCommandTypes.PlayMode,
            BridgeCommandTypes.EditMode,
            BridgeCommandTypes.ProfilerBrowse,
            BridgeCommandTypes.ProfilerOverview,
            BridgeCommandTypes.ProfilerRecord,
            BridgeCommandTypes.Record,
            BridgeCommandTypes.RefreshAssetDatabase,
            BridgeCommandTypes.ReimportAssets,
            BridgeCommandTypes.Reflect,
            BridgeCommandTypes.Restart,
            BridgeCommandTypes.RunTestsEditMode,
            BridgeCommandTypes.RunTestsPlayer,
            BridgeCommandTypes.RunTestsPlayMode,
            BridgeCommandTypes.SaveScenes,
            BridgeCommandTypes.Screenshot,
            BridgeCommandTypes.Search,
            BridgeCommandTypes.Show,
            BridgeCommandTypes.Status,
            BridgeCommandTypes.ToJson,
            BridgeCommandTypes.ViewBurstAsm,
        };

        internal static void WriteServer(string configPath, string serverExecutablePath)
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

        internal static bool IsApplied(string configPath, string expectedServerExecutablePath)
        {
            var document = ReadTomlDocument(configPath);
            string? enabled = GetTomlValue(document, "mcp_servers.unity", "enabled");
            return (enabled is null || enabled == "true")
                   && ServerExecutableLocator.CommandMatches(
                       UnquoteToml(GetTomlValue(document, "mcp_servers.unity", "command")),
                       expectedServerExecutablePath
                   );
        }

        internal static bool TryGetConfiguredExecutable(
            string configPath,
            out string executablePath
        )
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

                if (ServerExecutableLocator.TryResolveConfiguredExecutable(
                        UnquoteToml(line[(separatorIndex + 1)..].Trim()),
                        out executablePath
                    ))
                    return true;
            }

            return false;
        }

        internal static void WriteToolPermissions(string configPath)
        {
            var document = ReadTomlDocument(configPath);
            foreach (string tool in approvedTools)
                SetTomlKey(
                    document,
                    "mcp_servers.unity",
                    $"tools.{tool}.approval_mode",
                    "\"approve\""
                );

            WriteTomlDocument(configPath, document);
        }

        internal static bool HasToolPermissions(string configPath)
        {
            if (!File.Exists(configPath))
                return false;

            try
            {
                var document = ReadTomlDocument(configPath);
                foreach (string tool in approvedTools)
                    if (!string.Equals(
                            GetTomlValue(
                                document,
                                "mcp_servers.unity",
                                $"tools.{tool}.approval_mode"
                            ),
                            "\"approve\"",
                            StringComparison.Ordinal
                        ))
                        return false;

                return true;
            }
            catch
            {
                return false;
            }
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

        sealed class TomlDocument
        {
            internal List<string> Lines = new();
            internal Dictionary<string, int> Entries = new(StringComparer.Ordinal);
            internal int TableStart = -1;
            internal int TableEnd = -1;
        }
    }
}
