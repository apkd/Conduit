#nullable enable

using System;
using System.IO;

namespace Conduit
{
    static class JsonEditorConfiguration
    {
        internal static void Write(
            EditorClientSpec spec,
            string configPath,
            string serverExecutablePath
        )
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

        internal static bool IsApplied(
            EditorClientSpec spec,
            string configPath,
            string expectedServerExecutablePath
        )
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

                return ServerExecutableLocator.CommandMatches(command, expectedServerExecutablePath);
            }
        }

        internal static bool TryGetConfiguredExecutable(
            EditorClientSpec spec,
            string configPath,
            out string executablePath
        )
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

                if (!ServerExecutableLocator.TryResolveConfiguredExecutable(
                        command,
                        out executablePath
                    ))
                    continue;

                return true;
            }

            return false;
        }

    }
}
